using RxSuite.Domain.Common;

namespace RxSuite.Domain.Catalog;

// BRD 8.4 INV-01/02 (item master), 8.3 POS-03 (selling units), 8.6 RX-01 (link to the central drug
// database) and 8.7 CTL-01/08 (control schedule and limits).

#region Enums

/// <summary>POS-03: the packaging units an item can be sold in.</summary>
public enum UnitType : byte { Box = 1, Strip = 2, Unit = 3 }

/// <summary>CTL-01: the official control table a medicine is on. None for ordinary items.</summary>
public enum ControlSchedule : byte { None = 0, Narcotic = 1, Psychotropic = 2 }

#endregion

#region Item

/// <summary>
/// AGGREGATE ROOT. A product in the tenant's item master: a medicine or a local item
/// (cosmetic, supplement, device). Owns unit conversion and price rules.
/// </summary>
public sealed class Item : TenantEntity<int>
{
    #region State

    public string Code { get; private set; } = string.Empty;
    public string NameEn { get; private set; } = string.Empty;
    public string NameAr { get; private set; } = string.Empty;
    public string ScientificName { get; private set; } = string.Empty;
    public string Strength { get; private set; } = string.Empty;
    public string DosageForm { get; private set; } = string.Empty;
    public int StripsPerBox { get; private set; }
    public int UnitsPerStrip { get; private set; }

    /// <summary>Official EDA price of a box. Null for items EDA does not price (cosmetics...).</summary>
    public decimal? OfficialPrice { get; private set; }

    /// <summary>Selling price of a box, VAT included.</summary>
    public decimal SellingPrice { get; private set; }

    /// <summary>BR-POS-1: VAT rate of the item's tax category, e.g. 0 (exempt medicine) or 0.14.</summary>
    public decimal VatRate { get; private set; }

    /// <summary>ACC-02: the item's code at the tax authority (GS1 / EGS).</summary>
    public string? EtaItemCode { get; private set; }
    public bool IsRxOnly { get; private set; }

    /// <summary>CTL-01: the control table, normally copied from the central drug database.</summary>
    public ControlSchedule ControlSchedule { get; private set; }

    /// <summary>RX-01: the matching medicine in the central drug database. Null for local items.</summary>
    public int? DrugId { get; private set; }

    /// <summary>CTL-08: most a single prescription may receive, in the smallest unit. Null means no limit.</summary>
    public int? ControlledLimitPerPrescription { get; private set; }

    /// <summary>CTL-08: most one patient may receive in the limit window, in the smallest unit.</summary>
    public int? ControlledLimitPerPatient { get; private set; }

    /// <summary>INV-06: reorder level, in the smallest unit.</summary>
    public int ReorderLevel { get; private set; }
    public bool IsActive { get; private set; }

    private Item() { }

    #endregion

    #region Factory

    public static Item Create(int tenantId, string code, string nameEn, string nameAr, string scientificName,
        string strength, string dosageForm, int stripsPerBox, int unitsPerStrip, decimal? officialPrice,
        decimal sellingPrice, decimal vatRate, string? etaItemCode, bool isRxOnly, ControlSchedule controlSchedule,
        int reorderLevel, bool allowAboveOfficialPrice = false, int? drugId = null,
        int? controlledLimitPerPrescription = null, int? controlledLimitPerPatient = null)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new DomainException("The item code is required.");
        if (string.IsNullOrWhiteSpace(nameEn) && string.IsNullOrWhiteSpace(nameAr))
            throw new DomainException("The item name is required.");
        if (stripsPerBox < 1 || unitsPerStrip < 1)
            throw new DomainException("Strips per box and units per strip must be at least 1.");
        if (vatRate is < 0 or > 1) throw new DomainException("The VAT rate must be between 0 and 1.");
        if (reorderLevel < 0) throw new DomainException("The reorder level cannot be negative.");
        if (!Enum.IsDefined(controlSchedule)) throw new DomainException("The control schedule is not valid.");
        if (controlSchedule == ControlSchedule.None && (controlledLimitPerPrescription is not null || controlledLimitPerPatient is not null))
            throw new DomainException("Dispensing limits apply only to controlled items.");
        if (controlledLimitPerPrescription <= 0 || controlledLimitPerPatient <= 0)
            throw new DomainException("A dispensing limit must be greater than zero.");

        var item = new Item
        {
            TenantId = tenantId, Code = code.Trim(), NameEn = nameEn.Trim(), NameAr = nameAr.Trim(),
            ScientificName = scientificName.Trim(), Strength = strength.Trim(), DosageForm = dosageForm.Trim(),
            StripsPerBox = stripsPerBox, UnitsPerStrip = unitsPerStrip, OfficialPrice = officialPrice,
            VatRate = vatRate, EtaItemCode = etaItemCode?.Trim(),
            // A controlled medicine is always prescription-only.
            IsRxOnly = isRxOnly || controlSchedule != ControlSchedule.None, ControlSchedule = controlSchedule,
            DrugId = drugId, ControlledLimitPerPrescription = controlledLimitPerPrescription,
            ControlledLimitPerPatient = controlledLimitPerPatient, ReorderLevel = reorderLevel, IsActive = true
        };
        item.ChangePrice(sellingPrice, allowAboveOfficialPrice);
        return item;
    }

    #endregion

    #region Units (BR-INV-1: stock is always stored in the smallest unit)

    public int UnitsPerBox => StripsPerBox * UnitsPerStrip;

    /// <summary>How many smallest units one of the given packaging unit holds.</summary>
    public int BaseUnitsPer(UnitType unit) => unit switch
    {
        UnitType.Box => UnitsPerBox,
        UnitType.Strip => UnitsPerStrip,
        _ => 1
    };

    public int ToBaseUnits(int quantity, UnitType unit) => quantity * BaseUnitsPer(unit);

    #endregion

    #region Prices

    /// <summary>
    /// BR-POS-3 / AC-POS-3: a strip or unit costs its share of the box price, rounded to the step.
    /// A box of 3 strips at 90 EGP gives 30 EGP per strip.
    /// </summary>
    public decimal PriceFor(UnitType unit, decimal roundingStep, decimal? branchBoxPrice = null)
    {
        // MBR-02: a branch may sell at its own box price; strips and units follow it the same way.
        var boxPrice = branchBoxPrice ?? SellingPrice;
        return unit == UnitType.Box ? boxPrice : Money.RoundTo(boxPrice * BaseUnitsPer(unit) / UnitsPerBox, roundingStep);
    }

    /// <summary>BR-POS-2: the selling price may not exceed the official EDA price without the override permission.</summary>
    public void ChangePrice(decimal newPrice, bool allowAboveOfficialPrice)
    {
        if (newPrice <= 0) throw new DomainException("The selling price must be greater than zero.");
        if (OfficialPrice is { } official && newPrice > official && !allowAboveOfficialPrice)
            throw new DomainException($"The selling price cannot exceed the official price ({official:0.00} EGP).");

        SellingPrice = newPrice;
    }

    #endregion

    #region Dispensing rules

    /// <summary>CTL-01: the item is on a narcotic or psychotropic table.</summary>
    public bool IsControlled => ControlSchedule != ControlSchedule.None;

    /// <summary>Rx-only and controlled items can only leave the pharmacy through a licensed pharmacist.</summary>
    public bool RequiresPharmacist => IsRxOnly || IsControlled;

    /// <summary>The name printed on receipts, labels and the register.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(NameEn) ? NameAr : NameEn;

    #endregion
}

#endregion
