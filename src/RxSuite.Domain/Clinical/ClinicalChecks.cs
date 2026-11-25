using RxSuite.Domain.Common;

namespace RxSuite.Domain.Clinical;

// BRD 8.6 RX-02 / RX-03 / BR-RX-2 / AC-RX-1: the clinical checks run before a medicine is dispensed.
// The data comes from the central drug database (RX-01, RX-11); the rules live here, as pure code.

#region Enums

/// <summary>RX-02: interaction severity. The order matters: higher is worse.</summary>
public enum AlertSeverity : byte { Minor = 1, Moderate = 2, Major = 3, Contraindicated = 4 }

/// <summary>The kinds of check RX-02 and CTL-08 ask for.</summary>
public enum AlertType : byte
{
    DrugInteraction = 1, Allergy = 2, DuplicateTherapy = 3, AgeWarning = 4, MaxDailyDose = 5, ControlledLimit = 6
}

#endregion

#region Drug data (read from the central database)

/// <summary>One active ingredient of a medicine, with the numbers the checks need.</summary>
/// <param name="Code">Stable ingredient code, e.g. AMOXICILLIN.</param>
/// <param name="ClassCode">Therapeutic or allergy class, e.g. PENICILLINS. Used for class allergies and duplicate therapy.</param>
/// <param name="StrengthMgPerUnit">Milligrams in one smallest unit (one tablet, one ml...). Null when not given.</param>
/// <param name="MaxDailyDoseMg">Largest adult daily dose. Null when the database has none.</param>
/// <param name="MinAgeYears">Youngest age the ingredient is approved for. Null when there is no limit.</param>
public sealed record IngredientInfo(string Code, string Name, string? ClassCode, decimal? StrengthMgPerUnit,
    decimal? MaxDailyDoseMg, int? MinAgeYears);

/// <summary>What the checks know about one tenant item: its name and the ingredients of its central drug.</summary>
public sealed record ItemClinicalProfile(int ItemId, string ItemName, IReadOnlyList<IngredientInfo> Ingredients);

/// <summary>RX-02: an interaction between two ingredients, from the central database.</summary>
public sealed record DrugInteraction(string IngredientA, string IngredientB, AlertSeverity Severity,
    string Description, string? Recommendation);

#endregion

#region Inputs and outputs

/// <summary>An item about to be dispensed. The dose is known when it comes from a prescription line.</summary>
/// <param name="DoseUnits">Smallest units per dose, e.g. 1 tablet or 5 ml.</param>
public sealed record ItemToCheck(int ItemId, decimal? DoseUnits = null, int? FrequencyPerDay = null);

/// <summary>What the checks need to know about the patient.</summary>
/// <param name="AllergyCodes">Ingredient or class codes the patient is allergic to (US-RX-3).</param>
/// <param name="CurrentMedicationItemIds">Items dispensed to the patient recently, from any branch (RX-08).</param>
public sealed record PatientContext(DateOnly? BirthDate, IReadOnlyCollection<string> AllergyCodes,
    IReadOnlyCollection<int> CurrentMedicationItemIds);

/// <summary>
/// One alert for the alerts panel (RX-03). <see cref="Code"/> is stable for the same situation, so a
/// pharmacist's override can be matched to the alert it answers.
/// </summary>
public sealed record ClinicalAlert(string Code, AlertType Type, AlertSeverity Severity, IReadOnlyList<int> ItemIds,
    string Message, string? Recommendation)
{
    /// <summary>BR-RX-2: contraindicated and major alerts need an override with a reason.</summary>
    public bool RequiresOverride => Severity >= AlertSeverity.Major;
}

/// <summary>A pharmacist's decision to go ahead despite an alert (RX-03, BR-RX-2).</summary>
public sealed record AlertOverride(string AlertCode, string Reason);

#endregion

#region Clinical checker (domain service)

/// <summary>
/// RX-02: drug–drug interactions, drug–allergy, duplicate therapy, age warnings and maximum
/// daily dose. A pure function over the data it is given, so every rule can be tested alone.
/// </summary>
public static class ClinicalChecker
{
    public static IReadOnlyList<ClinicalAlert> Check(
        IReadOnlyList<ItemToCheck> items, IReadOnlyDictionary<int, ItemClinicalProfile> profiles,
        PatientContext patient, IReadOnlyCollection<DrugInteraction> interactions, DateOnly today)
    {
        var alerts = new List<ClinicalAlert>();
        var newItems = items.Where(i => profiles.ContainsKey(i.ItemId)).ToList();

        // Each item once, so two lines of the same item do not raise the same alert twice.
        var newProfiles = newItems.Select(i => profiles[i.ItemId]).DistinctBy(p => p.ItemId).ToList();
        var currentProfiles = patient.CurrentMedicationItemIds
            .Where(id => profiles.ContainsKey(id) && newProfiles.All(p => p.ItemId != id))
            .Select(id => profiles[id]).ToList();

        AddAllergyAlerts(alerts, newProfiles, patient.AllergyCodes);
        AddInteractionAlerts(alerts, newProfiles, currentProfiles, interactions);
        AddDuplicateAlerts(alerts, newProfiles, currentProfiles);
        AddAgeAlerts(alerts, newProfiles, patient.BirthDate, today);
        AddDoseAlerts(alerts, newItems, profiles);

        return alerts.OrderByDescending(a => a.Severity).ThenBy(a => a.Code, StringComparer.Ordinal).ToList();
    }

    /// <summary>BR-RX-2: the alerts that need an override but did not get one with a reason.</summary>
    public static IReadOnlyList<ClinicalAlert> MissingOverrides(IEnumerable<ClinicalAlert> alerts,
        IEnumerable<AlertOverride> overrides)
    {
        var answered = overrides.Where(o => !string.IsNullOrWhiteSpace(o.Reason))
            .Select(o => o.AlertCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return alerts.Where(a => a.RequiresOverride && !answered.Contains(a.Code)).ToList();
    }

    #region Rules

    /// <summary>AC-RX-1: an allergy to the ingredient or to its class (penicillin allergy + amoxicillin).</summary>
    private static void AddAllergyAlerts(List<ClinicalAlert> alerts, List<ItemClinicalProfile> items,
        IReadOnlyCollection<string> allergyCodes)
    {
        var allergies = allergyCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allergies.Count == 0) return;

        foreach (var item in items)
            foreach (var ingredient in item.Ingredients)
            {
                var matched = allergies.Contains(ingredient.Code) ? ingredient.Code
                    : ingredient.ClassCode is { } cls && allergies.Contains(cls) ? cls : null;
                if (matched is null) continue;

                alerts.Add(new ClinicalAlert($"ALLERGY:{item.ItemId}:{ingredient.Code}", AlertType.Allergy,
                    AlertSeverity.Contraindicated, [item.ItemId],
                    $"The patient is allergic to {matched}; {item.ItemName} contains {ingredient.Name}.",
                    "Do not dispense. Contact the prescriber for an alternative."));
            }
    }

    /// <summary>Interactions inside the new items, and between the new items and current medicines.</summary>
    private static void AddInteractionAlerts(List<ClinicalAlert> alerts, List<ItemClinicalProfile> newItems,
        List<ItemClinicalProfile> currentItems, IReadOnlyCollection<DrugInteraction> interactions)
    {
        if (interactions.Count == 0) return;

        var pairs = new List<(ItemClinicalProfile A, ItemClinicalProfile B, bool WithCurrent)>();
        for (var i = 0; i < newItems.Count; i++)
        {
            for (var j = i + 1; j < newItems.Count; j++) pairs.Add((newItems[i], newItems[j], false));
            foreach (var current in currentItems) pairs.Add((newItems[i], current, true));
        }

        foreach (var (a, b, withCurrent) in pairs)
            foreach (var ia in a.Ingredients)
                foreach (var ib in b.Ingredients)
                {
                    var hit = interactions.FirstOrDefault(x =>
                        (Same(x.IngredientA, ia.Code) && Same(x.IngredientB, ib.Code)) ||
                        (Same(x.IngredientA, ib.Code) && Same(x.IngredientB, ia.Code)));
                    if (hit is null) continue;

                    // The same two ingredients give the same code whichever item comes first.
                    var key = string.CompareOrdinal(ia.Code, ib.Code) <= 0 ? $"{ia.Code}+{ib.Code}" : $"{ib.Code}+{ia.Code}";
                    var source = withCurrent ? $" (the patient already takes {b.ItemName})" : string.Empty;

                    alerts.Add(new ClinicalAlert($"INTERACTION:{key}", AlertType.DrugInteraction, hit.Severity,
                        [a.ItemId, b.ItemId], $"{a.ItemName} and {b.ItemName}{source}: {hit.Description}",
                        hit.Recommendation));
                }
    }

    /// <summary>RX-02 duplicate therapy: the same ingredient (major) or the same class (moderate) twice.</summary>
    private static void AddDuplicateAlerts(List<ClinicalAlert> alerts, List<ItemClinicalProfile> newItems,
        List<ItemClinicalProfile> currentItems)
    {
        var all = newItems.Select(p => (Profile: p, IsNew: true)).Concat(currentItems.Select(p => (Profile: p, IsNew: false))).ToList();

        List<ClinicalAlert> Find(Func<IngredientInfo, string?> keyOf, AlertSeverity severity, string what) => all
            .SelectMany(x => x.Profile.Ingredients.Select(keyOf).OfType<string>().Distinct()
                .Select(key => (Key: key, x.Profile, x.IsNew)))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Profile.ItemId).Distinct().Count() > 1 && g.Any(x => x.IsNew))
            .Select(g => new ClinicalAlert($"DUPLICATE:{g.Key.ToUpperInvariant()}", AlertType.DuplicateTherapy, severity,
                g.Select(x => x.Profile.ItemId).Distinct().ToList(),
                $"Duplicate therapy: {string.Join(", ", g.Select(x => x.Profile.ItemName).Distinct())} share the same {what} ({g.Key}).",
                "Check that both are intended; usually only one should be taken."))
            .ToList();

        var sameIngredient = Find(i => i.Code, AlertSeverity.Major, "ingredient");
        alerts.AddRange(sameIngredient);

        // The same class is reported only for items not already caught by the same ingredient.
        var covered = sameIngredient.SelectMany(a => a.ItemIds).ToHashSet();
        alerts.AddRange(Find(i => i.ClassCode, AlertSeverity.Moderate, "class")
            .Where(a => !a.ItemIds.All(covered.Contains)));
    }

    /// <summary>RX-02 age-related warnings.</summary>
    private static void AddAgeAlerts(List<ClinicalAlert> alerts, List<ItemClinicalProfile> items, DateOnly? birthDate,
        DateOnly today)
    {
        if (birthDate is not { } born) return;
        var age = today.Year - born.Year - (today < born.AddYears(today.Year - born.Year) ? 1 : 0);

        foreach (var item in items)
            foreach (var ingredient in item.Ingredients.Where(i => i.MinAgeYears is { } min && age < min))
                alerts.Add(new ClinicalAlert($"AGE:{item.ItemId}:{ingredient.Code}", AlertType.AgeWarning,
                    AlertSeverity.Major, [item.ItemId],
                    $"{item.ItemName}: {ingredient.Name} is not approved under {ingredient.MinAgeYears} years (patient is {age}).",
                    "Confirm the dose and the indication with the prescriber."));
    }

    /// <summary>RX-02 maximum daily dose, when the prescription gives dose and frequency.</summary>
    private static void AddDoseAlerts(List<ClinicalAlert> alerts, List<ItemToCheck> items,
        IReadOnlyDictionary<int, ItemClinicalProfile> profiles)
    {
        foreach (var item in items.Where(i => i is { DoseUnits: > 0, FrequencyPerDay: > 0 }))
        {
            var profile = profiles[item.ItemId];
            foreach (var ingredient in profile.Ingredients)
            {
                if (ingredient is not { StrengthMgPerUnit: { } mg, MaxDailyDoseMg: { } max }) continue;

                var daily = item.DoseUnits!.Value * item.FrequencyPerDay!.Value * mg;
                if (daily <= max) continue;

                alerts.Add(new ClinicalAlert($"MAXDOSE:{item.ItemId}:{ingredient.Code}", AlertType.MaxDailyDose,
                    AlertSeverity.Major, [item.ItemId],
                    $"{profile.ItemName}: {daily:0.##} mg of {ingredient.Name} a day is above the maximum of {max:0.##} mg.",
                    "Check the dose and frequency with the prescriber."));
            }
        }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    #endregion
}

#endregion
