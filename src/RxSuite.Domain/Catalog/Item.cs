using RxSuite.Domain.Common;

namespace RxSuite.Domain.Catalog;

// BRD 8.4 INV-01/02 (item master) and 8.3 POS-03 (selling units).

#region Enums

/// <summary>POS-03: the packaging units an item can be sold in.</summary>
public enum UnitType : byte { Box = 1, Strip = 2, Unit = 3 }

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
    public bool IsControlled { get; private set; }

    /// <summary>INV-06: reorder level, in the smallest unit.</summary>
    public int ReorderLevel { get; private set; }
    public bool IsActive { get; private set; }

    private Item() { }

    #endregion

    #region Factory

    public static Item Create(int tenantId, string code, string nameEn, string nameAr, string scientificName,
        string strength, string dosageForm, int stripsPerBox, int unitsPerStrip, decimal? officialPrice,
        decimal sellingPrice, decimal vatRate, string? etaItemCode, bool isRxOnly, bool isControlled,
        int reorderLevel, bool allowAboveOfficialPrice = false)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new DomainException("The item code is required.");
        if (string.IsNullOrWhiteSpace(nameEn) && string.IsNullOrWhiteSpace(nameAr))
            throw new DomainException("The item name is required.");
        if (stripsPerBox < 1 || unitsPerStrip < 1)
            throw new DomainException("Strips per box and units per strip must be at least 1.");
        if (vatRate is < 0 or > 1) throw new DomainException("The VAT rate must be between 0 and 1.");
        if (reorderLevel < 0) throw new DomainException("The reorder level cannot be negative.");

        var item = new Item
        {
            TenantId = tenantId, Code = code.Trim(), NameEn = nameEn.Trim(), NameAr = nameAr.Trim(),
            ScientificName = scientificName.Trim(), Strength = strength.Trim(), DosageForm = dosageForm.Trim(),
            StripsPerBox = stripsPerBox, UnitsPerStrip = unitsPerStrip, OfficialPrice = officialPrice,
            VatRate = vatRate, EtaItemCode = etaItemCode?.Trim(), IsRxOnly = isRxOnly,
            IsControlled = isControlled, ReorderLevel = reorderLevel, IsActive = true
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
    public decimal PriceFor(UnitType unit, decimal roundingStep) => unit == UnitType.Box
        ? SellingPrice
        : Money.RoundTo(SellingPrice * BaseUnitsPer(unit) / UnitsPerBox, roundingStep);

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

    /// <summary>Rx-only and controlled items can only leave the pharmacy through a licensed pharmacist.</summary>
    public bool RequiresPharmacist => IsRxOnly || IsControlled;

    #endregion
}

#endregion
