using RxSuite.Domain.Common;

namespace RxSuite.Domain.Sales;

// BRD 8.3 POS-16 and Figure 11 "Cash shift open/close flow".

#region Enums and totals

public enum ShiftStatus : byte { Open = 1, Closed = 2 }

public enum CashMovementType : byte { PayIn = 1, PayOut = 2 }

/// <summary>The cash that went through the drawer during a shift, summed by the database.</summary>
public sealed record ShiftCashTotals(decimal CashSales, decimal CashRefunds, decimal PayIns, decimal PayOuts);

#endregion

#region Shift

/// <summary>
/// AGGREGATE ROOT. A cashier's work session on a terminal. Every sale belongs to an open shift,
/// and closing compares the counted cash with the expected cash.
/// </summary>
public sealed class Shift : TenantEntity<int>
{
    #region State

    public int BranchId { get; private set; }
    public int CashierId { get; private set; }
    public string TerminalCode { get; private set; } = string.Empty;
    public decimal OpeningFloat { get; private set; }
    public ShiftStatus Status { get; private set; }
    public DateTime OpenedAtUtc { get; private set; }
    public DateTime? ClosedAtUtc { get; private set; }
    public decimal? ExpectedCash { get; private set; }
    public decimal? CountedCash { get; private set; }
    public decimal? Variance { get; private set; }
    public string? ClosingNote { get; private set; }
    public int? ApprovedBy { get; private set; }

    private Shift() { }

    #endregion

    #region Factory

    public static Shift Open(int tenantId, int branchId, int cashierId, string terminalCode, decimal openingFloat, DateTime nowUtc)
    {
        if (openingFloat < 0) throw new DomainException("The opening float cannot be negative.");
        if (string.IsNullOrWhiteSpace(terminalCode)) throw new DomainException("The terminal is required.");

        return new Shift
        {
            TenantId = tenantId, BranchId = branchId, CashierId = cashierId, TerminalCode = terminalCode.Trim(),
            OpeningFloat = openingFloat, Status = ShiftStatus.Open, OpenedAtUtc = nowUtc
        };
    }

    #endregion

    #region Behaviour

    public bool IsOpen => Status == ShiftStatus.Open;

    /// <summary>Expected cash in the drawer = float + cash sales - cash refunds + pay-ins - pay-outs.</summary>
    public decimal ExpectedCashFor(ShiftCashTotals totals) =>
        OpeningFloat + totals.CashSales - totals.CashRefunds + totals.PayIns - totals.PayOuts;

    /// <summary>Counted minus expected. Positive = more cash than expected.</summary>
    public decimal VarianceFor(decimal countedCash, ShiftCashTotals totals) => countedCash - ExpectedCashFor(totals);

    /// <summary>
    /// Closes the shift. A variance above the tolerance must carry a note and a manager's
    /// sign-off (Figure 11); the caller checks the manager before passing <paramref name="approvedBy"/>.
    /// </summary>
    public void Close(decimal countedCash, ShiftCashTotals totals, decimal tolerance, string? note, int? approvedBy, DateTime nowUtc)
    {
        if (!IsOpen) throw new DomainException("The shift is already closed.");
        if (countedCash < 0) throw new DomainException("Counted cash cannot be negative.");

        var variance = VarianceFor(countedCash, totals);
        if (variance != 0 && string.IsNullOrWhiteSpace(note))
            throw new DomainException("A cash variance needs a note from the cashier.");
        if (Math.Abs(variance) > tolerance && approvedBy is null)
            throw new DomainException("The cash variance is above the tolerance and needs a manager's sign-off.");

        ExpectedCash = ExpectedCashFor(totals);
        CountedCash = countedCash;
        Variance = variance;
        ClosingNote = note?.Trim();
        ApprovedBy = approvedBy;
        ClosedAtUtc = nowUtc;
        Status = ShiftStatus.Closed;
    }

    #endregion
}

#endregion
