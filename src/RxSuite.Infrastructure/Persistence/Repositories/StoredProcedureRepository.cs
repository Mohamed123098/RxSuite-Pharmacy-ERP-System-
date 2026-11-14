using System.Data;
using System.Text.Json;
using Dapper;
using RxSuite.Application.Abstractions;
using RxSuite.Infrastructure.Persistence.Connection;

namespace RxSuite.Infrastructure.Persistence.Repositories;

/// <summary>
/// TEMPLATE for every repository. It owns the single way a stored procedure is executed: same
/// connection, same transaction, CommandType.StoredProcedure and cancellation every time.
/// Concrete repositories only say WHICH procedure and WHICH parameters (DRY).
/// Parameters are always passed as objects, never concatenated into text, so SQL injection is
/// not possible through this class. No SQL text exists anywhere in the C# code.
/// </summary>
internal abstract class StoredProcedureRepository(IDbSession session, ICurrentUser currentUser)
{
    #region Tenant isolation (NFR-05)

    /// <summary>
    /// Adds @TenantId of the logged-in user to the parameters. Repositories use it for every
    /// tenant-owned procedure, so the tenant filter cannot be forgotten or chosen by the caller.
    /// </summary>
    protected DynamicParameters Scoped(object? parameters = null)
    {
        if (!currentUser.IsAuthenticated)
            throw new InvalidOperationException("A tenant-scoped procedure was called without a logged-in user.");

        var scoped = new DynamicParameters(parameters);
        scoped.Add("TenantId", currentUser.TenantId);
        return scoped;
    }

    /// <summary>The same as <see cref="Scoped"/>, plus @UserId for procedures that record who acted.</summary>
    protected DynamicParameters ScopedWithUser(object? parameters = null)
    {
        var scoped = Scoped(parameters);
        scoped.Add("UserId", currentUser.UserId);
        return scoped;
    }

    /// <summary>Child rows (lines, payments) travel to a procedure as one JSON parameter, read there with OPENJSON.</summary>
    protected static string Json<T>(IEnumerable<T> rows) => JsonSerializer.Serialize(rows);

    #endregion

    #region Read helpers

    /// <summary>Procedure that returns many rows.</summary>
    protected async Task<IReadOnlyList<T>> QueryAsync<T>(string procedure, object? parameters, CancellationToken ct)
    {
        var connection = await session.GetConnectionAsync(ct);
        return (await connection.QueryAsync<T>(Command(procedure, parameters, ct))).ToList();
    }

    /// <summary>Procedure that returns one row, or none.</summary>
    protected async Task<T?> QuerySingleOrDefaultAsync<T>(string procedure, object? parameters, CancellationToken ct)
    {
        var connection = await session.GetConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<T>(Command(procedure, parameters, ct));
    }

    /// <summary>Procedure that returns several result sets (a header and its lines, or a page and its count).</summary>
    protected async Task<TResult> QueryMultipleAsync<TResult>(string procedure, object? parameters,
        Func<SqlMapper.GridReader, Task<TResult>> read, CancellationToken ct)
    {
        var connection = await session.GetConnectionAsync(ct);
        using var grid = await connection.QueryMultipleAsync(Command(procedure, parameters, ct));
        return await read(grid);
    }

    #endregion

    #region Write helpers

    /// <summary>Procedure that returns a single value (a new identity, a flag, a count).</summary>
    protected async Task<T> ExecuteScalarAsync<T>(string procedure, object? parameters, CancellationToken ct)
    {
        var connection = await session.GetConnectionAsync(ct);
        return (await connection.ExecuteScalarAsync<T>(Command(procedure, parameters, ct)))!;
    }

    /// <summary>Procedure that returns nothing.</summary>
    protected async Task ExecuteAsync(string procedure, object? parameters, CancellationToken ct)
    {
        var connection = await session.GetConnectionAsync(ct);
        await connection.ExecuteAsync(Command(procedure, parameters, ct));
    }

    #endregion

    #region Command builder

    private CommandDefinition Command(string procedure, object? parameters, CancellationToken ct) =>
        new(procedure, parameters, session.Transaction, commandType: CommandType.StoredProcedure, cancellationToken: ct);

    #endregion
}
