using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Data.SqlClient;
using RxSuite.Application.Abstractions;

namespace RxSuite.Infrastructure.Persistence.Connection;

#region Connection factory (design pattern: Factory)

/// <summary>FACTORY. Creates database connections so no other class knows the connection string or the vendor.</summary>
internal interface IDbConnectionFactory
{
    /// <summary>Returns a new, closed connection. The caller opens and disposes it.</summary>
    DbConnection Create();
}

/// <summary>The only class in the solution that names SQL Server.</summary>
internal sealed class SqlConnectionFactory(string connectionString) : IDbConnectionFactory
{
    public DbConnection Create() => new SqlConnection(connectionString);
}

#endregion

#region Session and Unit of Work

/// <summary>The connection and transaction that all repositories of one request share.</summary>
internal interface IDbSession
{
    ValueTask<DbConnection> GetConnectionAsync(CancellationToken cancellationToken);
    DbTransaction? Transaction { get; }
}

/// <summary>
/// UNIT OF WORK for Dapper. One instance lives per request (scoped):
/// - the connection opens lazily, on first use, and closes when the request ends;
/// - while a transaction is open every repository call joins it, because the repositories read
///   <see cref="Transaction"/> from this same object.
/// </summary>
internal sealed class DbSession(IDbConnectionFactory connectionFactory) : IDbSession, IUnitOfWork, IAsyncDisposable
{
    private DbConnection? _connection;

    public DbTransaction? Transaction { get; private set; }

    #region IDbSession

    public async ValueTask<DbConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null) return _connection;

        _connection = connectionFactory.Create();
        await _connection.OpenAsync(cancellationToken);
        return _connection;
    }

    #endregion

    #region IUnitOfWork

    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        if (Transaction is not null) throw new InvalidOperationException("A transaction is already open.");

        var connection = await GetConnectionAsync(cancellationToken);
        Transaction = await connection.BeginTransactionAsync(cancellationToken);
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (Transaction is null) throw new InvalidOperationException("There is no open transaction to commit.");

        await Transaction.CommitAsync(cancellationToken);
        await DisposeTransactionAsync();
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (Transaction is null) return;

        await Transaction.RollbackAsync(cancellationToken);
        await DisposeTransactionAsync();
    }

    #endregion

    #region Cleanup

    private async Task DisposeTransactionAsync()
    {
        if (Transaction is null) return;

        await Transaction.DisposeAsync();
        Transaction = null;
    }

    /// <summary>Runs at the end of the request. An uncommitted transaction is rolled back by SQL Server.</summary>
    public async ValueTask DisposeAsync()
    {
        await DisposeTransactionAsync();

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    #endregion
}

#endregion

#region Dapper type handlers

/// <summary>
/// Teaches Dapper to read and write <see cref="DateOnly"/> as a SQL DATE, so the domain can use
/// real dates for expiry and renewals. Registered once at start-up.
/// </summary>
internal sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
{
    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value = value.ToDateTime(TimeOnly.MinValue);
    }

    public override DateOnly Parse(object value) => DateOnly.FromDateTime((DateTime)value);

    public static void Register() => SqlMapper.AddTypeHandler(new DateOnlyHandler());
}

#endregion
