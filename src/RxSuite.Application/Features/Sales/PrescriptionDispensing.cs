using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Application.Features.Clinical;
using RxSuite.Domain.Catalog;
using RxSuite.Domain.Clinical;
using RxSuite.Domain.Common;
using RxSuite.Domain.Controlled;
using RxSuite.Domain.Rules;

namespace RxSuite.Application.Features.Sales;

// The prescription and controlled-drug rules of a sale (BRD 8.6, 8.7), kept out of the sale handler
// so each stays readable. Decides BEFORE anything is saved:
//   BR-RX-1   an Rx-only item needs a linked prescription (or "record later" for a pharmacist)
//   CTL-02/03 a controlled item needs a pharmacist at the till and a complete prescription
//   RX-02/03  clinical alerts; BR-RX-2 major and contraindicated ones need an override with a reason
//   CTL-08    per-prescription and per-patient limits, raised as alerts that need an override
//   RX-06     what is taken off each prescription line

#region Plan

/// <summary>What the sale must also save when it goes through.</summary>
internal sealed record DispensingPlan(Prescription? Prescription, Dispense? Dispense, DispenseParty? Party,
    IReadOnlyList<ClinicalAlert> Alerts, bool PrescriptionPending)
{
    public static readonly DispensingPlan None = new(null, null, null, [], false);
}

internal interface IDispensingPlanner
{
    /// <param name="pharmacistId">The pharmacist on the sale: the seller, or the one who approved with a PIN.</param>
    /// <param name="sellerIsPharmacist">The logged-in seller is a licensed pharmacist (CTL-02 needs exactly this).</param>
    Task<Result<DispensingPlan>> PlanAsync(CreateSaleCommand c, IReadOnlyDictionary<int, Item> itemsById,
        int? pharmacistId, bool sellerIsPharmacist, CancellationToken ct);
}

#endregion

#region Implementation

internal sealed class DispensingPlanner(
    IPrescriptionRepository prescriptions, IPatientRepository patients, IPrescriberRepository prescribers,
    IControlledRegisterRepository register, IClinicalCheckService checks, ICurrentUser currentUser, IClock clock,
    BusinessRules rules) : IDispensingPlanner
{
    private sealed record Link(int SaleLineNumber, SaleLineRequest Line, Item Item, PrescriptionLine? RxLine)
    {
        public int BaseUnits => Item.ToBaseUnits(Line.Quantity, Line.Unit);
    }

    public async Task<Result<DispensingPlan>> PlanAsync(CreateSaleCommand c, IReadOnlyDictionary<int, Item> itemsById,
        int? pharmacistId, bool sellerIsPharmacist, CancellationToken ct)
    {
        if (c.PrescriptionId is null && c.Lines.All(l => !itemsById[l.ItemId].IsRxOnly)) return DispensingPlan.None;

        #region 1. The prescription, its patient and its prescriber

        Prescription? rx = null;
        Patient? patient = null;
        Prescriber? prescriber = null;

        if (c.PrescriptionId is { } prescriptionId)
        {
            rx = await prescriptions.GetByIdAsync(prescriptionId, ct);
            if (rx is null) return Errors.NotFound("Prescription", prescriptionId);
            if (!rx.CanDispense) return Error.Conflict("Rx.NotDispensable", $"This prescription is {rx.Status}.");

            patient = await patients.GetByIdAsync(rx.PatientId, ct);
            prescriber = await prescribers.GetByIdAsync(rx.PrescriberId, ct);
            if (patient is null || prescriber is null) return Errors.NotFound("Prescription", prescriptionId);
        }

        #endregion

        #region 2. Link sale lines to prescription lines

        var links = new List<Link>();
        foreach (var (line, index) in c.Lines.Select((l, i) => (l, i)))
        {
            var item = itemsById[line.ItemId];
            PrescriptionLine? rxLine = null;

            if (line.PrescriptionLineNumber is { } number)
            {
                if (rx is null) return Error.Validation("Rx.NoPrescription", "A line points to a prescription, but none was given.");
                rxLine = rx.Lines.FirstOrDefault(x => x.LineNumber == number);
                if (rxLine is null || rxLine.ItemId != item.Id)
                    return Error.Validation("Rx.LineMismatch", $"Prescription line {number} is not for {item.DisplayName}.");
            }
            else if (rx is not null)
            {
                // Linked automatically when exactly one open prescription line is for this item.
                var matches = rx.Lines.Where(x => x.ItemId == item.Id && x.RemainingBaseUnits > 0).ToList();
                if (matches.Count == 1) rxLine = matches[0];
            }

            links.Add(new Link(index + 1, line, item, rxLine));
        }

        #endregion

        #region 3. BR-RX-1: Rx-only items need a prescription

        var pending = false;
        foreach (var link in links.Where(l => l.Item.IsRxOnly && l.RxLine is null))
        {
            // CTL-03: never "record later" for a controlled item.
            if (link.Item.IsControlled || !(rules.Clinical.AllowRecordPrescriptionLater && sellerIsPharmacist))
                return Errors.PrescriptionRequired(link.Item.DisplayName);
            pending = true;
        }

        #endregion

        #region 4. CTL-02 / CTL-03: controlled items

        DispenseParty? party = null;
        var controlled = links.Where(l => l.Item.IsControlled).ToList();
        if (controlled.Count > 0)
        {
            if (!sellerIsPharmacist) return Errors.PharmacistOnly;

            party = new DispenseParty(patient!.Id, patient.FullName, patient.NationalId, prescriber!.FullName,
                prescriber.LicenceNumber, rx!.Id, rx.PrescriptionNumber, rx.PrescribedOn, rx.Images.FirstOrDefault()?.Id);

            var missing = party.MissingDetails();
            if (missing.Count > 0)
                return Error.Conflict("Controlled.DetailsMissing",
                    $"A controlled medicine needs {string.Join(", ", missing)} before it is dispensed.");
        }

        #endregion

        #region 5. RX-02 clinical checks and CTL-08 limits; BR-RX-2 overrides

        var alerts = new List<ClinicalAlert>();
        if (patient is not null)
        {
            alerts.AddRange(await checks.CheckAsync(patient,
                links.Select(l => new ItemToCheck(l.Item.Id, l.RxLine?.DoseUnits, l.RxLine?.FrequencyPerDay)).ToList(), rx!.Id, ct));

            foreach (var group in controlled.GroupBy(l => l.Item.Id))
            {
                var item = group.First().Item;
                var units = group.Sum(l => l.BaseUnits);

                if (item.ControlledLimitPerPrescription is { } perRx
                    && rx!.Lines.Where(x => x.ItemId == item.Id).Sum(x => x.DispensedBaseUnits) + units > perRx)
                    alerts.Add(LimitAlert("RX", item, perRx, "per prescription"));

                if (item.ControlledLimitPerPatient is { } perPatient
                    && await register.GetDispensedToPatientAsync(patient.Id, item.Id,
                        clock.UtcNow.AddDays(-rules.Controlled.PatientLimitWindowDays), ct) + units > perPatient)
                    alerts.Add(LimitAlert("PATIENT", item, perPatient,
                        $"per patient in {rules.Controlled.PatientLimitWindowDays} days"));
            }

            var unanswered = ClinicalChecker.MissingOverrides(alerts, c.AlertOverrides ?? []);
            if (unanswered.Count > 0) return Errors.OverrideRequired(unanswered.Select(a => a.Code));
        }

        #endregion

        #region 6. RX-06: take the quantities off the prescription; build the dispense record

        if (rx is not null)
        {
            var parts = links.Where(l => l.RxLine is not null)
                .Select(l => (l.RxLine!.LineNumber, l.Item.Id, l.BaseUnits)).ToList();
            if (parts.Count == 0)
                return Error.Validation("Rx.NothingDispensed", "None of the sale lines is on the prescription.");
            rx.Dispense(parts);
        }

        // CreateSale always resolves a pharmacist when there is a prescription or an Rx-only item.
        if (pharmacistId is not { } dispensedBy) return Errors.PharmacistOnly;

        var dispense = Dispense.Create(currentUser.TenantId, c.BranchId, rx?.Id, patient?.Id, dispensedBy,
            links.Where(l => l.RxLine is not null || l.Item.IsRxOnly)
                .Select(l => new DispenseLine(l.RxLine?.LineNumber, l.SaleLineNumber, l.Item.Id, l.BaseUnits)),
            alerts, c.AlertOverrides ?? [], c.CounsellingNote, pending, clock.UtcNow);

        return new DispensingPlan(rx, dispense, party, alerts, pending);

        #endregion
    }

    private static ClinicalAlert LimitAlert(string scope, Item item, int limit, string what) =>
        new($"CTL-LIMIT-{scope}:{item.Id}", AlertType.ControlledLimit, AlertSeverity.Major, [item.Id],
            $"{item.DisplayName}: this sale goes above the limit of {limit} unit(s) {what}.",
            "Dispense only with a documented clinical reason.");
}

#endregion
