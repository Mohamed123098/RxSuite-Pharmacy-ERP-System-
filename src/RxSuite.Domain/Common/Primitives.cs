namespace RxSuite.Domain.Common;

#region Entities

/// <summary>Base type for every entity: an object identified by its Id rather than by its values.</summary>
public abstract class Entity<TId> where TId : struct
{
    /// <summary>Database identity. Zero means "not saved yet".</summary>
    public TId Id { get; protected set; }

    /// <summary>Called by a repository after the INSERT stored procedure returns the new key.</summary>
    public void AssignId(TId id) => Id = id;
}

/// <summary>
/// An entity that belongs to one tenant (one pharmacy, chain or hospital).
/// NFR-05 / BR-SUB-6: a tenant's data is never visible to another tenant.
/// </summary>
public abstract class TenantEntity<TId> : Entity<TId> where TId : struct
{
    public int TenantId { get; protected set; }
}

#endregion

#region Errors

/// <summary>
/// Thrown when a business invariant is broken (for example a negative quantity).
/// The API turns it into HTTP 400, so the message must be safe to show to a user.
/// </summary>
public sealed class DomainException(string message) : Exception(message);

#endregion

#region Small value types

/// <summary>"No value": the result type of a command that only succeeds or fails.</summary>
public readonly record struct Unit
{
    public static readonly Unit Value = new();
}

/// <summary>Money arithmetic in one place so every amount is rounded the same way (EGP, 2 decimals).</summary>
public static class Money
{
    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Rounds to the nearest step, e.g. 0.25 EGP for strip and unit prices (BR-POS-3).</summary>
    public static decimal RoundTo(decimal value, decimal step) =>
        step <= 0 ? Round(value) : Math.Round(value / step, 0, MidpointRounding.AwayFromZero) * step;
}

#endregion
