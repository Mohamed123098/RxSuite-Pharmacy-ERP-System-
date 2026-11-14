using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Domain.Common;

namespace RxSuite.Application.Pipeline;

// Every request travels the same road:
//   Dispatcher -> guards (who may do this?) -> validators (is the request well formed?)
//   -> transaction (commands only) -> handler.
// Cross-cutting rules live here once instead of being repeated in 30 handlers.

#region Dispatcher (Mediator)

internal sealed class Dispatcher(IServiceProvider provider) : IDispatcher
{
    public Task<Result<T>> SendAsync<T>(IRequest<T> request, CancellationToken cancellationToken)
    {
        // Close RequestPipeline<TRequest, T> over the runtime type of the request and resolve it.
        var pipelineType = typeof(RequestPipeline<,>).MakeGenericType(request.GetType(), typeof(T));
        var pipeline = (IRequestPipeline<T>)provider.GetRequiredService(pipelineType);
        return pipeline.RunAsync(request, cancellationToken);
    }
}

#endregion

#region Guards (design pattern: Chain of Responsibility)

/// <summary>
/// CHAIN OF RESPONSIBILITY. Each guard may stop a request with an error or let it pass to the
/// next. A new cross-cutting rule is a new guard class; nothing else changes (Open/Closed).
/// </summary>
public interface IRequestGuard
{
    Task<Error?> CheckAsync(object request, CancellationToken cancellationToken);
}

/// <summary>Reads the marker attributes of a request type once and caches them.</summary>
internal static class RequestTraits
{
    private static readonly ConcurrentDictionary<Type, (bool NoUser, string? Permission, bool NonTransactional)> Cache = new();

    public static (bool NoUser, string? Permission, bool NonTransactional) Of(Type requestType) =>
        Cache.GetOrAdd(requestType, type => (
            type.GetCustomAttribute<NoUserRequiredAttribute>() is not null,
            type.GetCustomAttribute<RequiresPermissionAttribute>()?.Permission,
            type.GetCustomAttribute<NonTransactionalAttribute>() is not null));
}

/// <summary>Guard 1: the caller is logged in and holds the permission the request asks for.</summary>
internal sealed class PermissionGuard(ICurrentUser currentUser) : IRequestGuard
{
    public Task<Error?> CheckAsync(object request, CancellationToken cancellationToken)
    {
        var traits = RequestTraits.Of(request.GetType());

        Error? error = traits switch
        {
            { NoUser: true } => null,
            _ when !currentUser.IsAuthenticated => Errors.NotLoggedIn,
            { Permission: { } permission } when !currentUser.Has(permission) => Errors.MissingPermission(permission),
            _ => null
        };
        return Task.FromResult(error);
    }
}

/// <summary>
/// Guard 2 (US-USR-5 / AC-USR-2): a user who was disabled is stopped on their very next request,
/// even though their access token has not expired yet. Costs one small read per request.
/// </summary>
internal sealed class ActiveUserGuard(ICurrentUser currentUser, IUserRepository users) : IRequestGuard
{
    public async Task<Error?> CheckAsync(object request, CancellationToken cancellationToken)
    {
        if (RequestTraits.Of(request.GetType()).NoUser) return null;

        var user = await users.GetByIdAsync(currentUser.UserId, cancellationToken);
        return user is { IsActive: true } ? null : Errors.NotLoggedIn;
    }
}

/// <summary>
/// Guard 3 (BR-SUB-4 / AC-SUB-2): a read-only tenant can still view and export, but every
/// command is refused, so POS selling is blocked.
/// </summary>
internal sealed class TenantWriteGuard(ICurrentUser currentUser, IPlatformRepository platform) : IRequestGuard
{
    public async Task<Error?> CheckAsync(object request, CancellationToken cancellationToken)
    {
        var isCommand = request.GetType().GetInterfaces()
            .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>));
        if (!isCommand || RequestTraits.Of(request.GetType()).NoUser) return null;

        var status = await platform.GetTenantStatusAsync(currentUser.TenantId, cancellationToken);
        return status is Domain.Platform.TenantStatus.Trial or Domain.Platform.TenantStatus.Active
            or Domain.Platform.TenantStatus.Grace
            ? null
            : Errors.TenantReadOnly;
    }
}

#endregion

#region Pipeline

internal interface IRequestPipeline<T>
{
    Task<Result<T>> RunAsync(IRequest<T> request, CancellationToken cancellationToken);
}

/// <summary>
/// DECORATOR around a handler: adds guards, validation and the transaction without the handler
/// knowing. Commands commit on success and roll back on a failure result or an exception.
/// </summary>
internal sealed class RequestPipeline<TRequest, T>(
    IEnumerable<IRequestGuard> guards,
    IEnumerable<IValidator<TRequest>> validators,
    IRequestHandler<TRequest, T> handler,
    IUnitOfWork unitOfWork) : IRequestPipeline<T>
    where TRequest : IRequest<T>
{
    public async Task<Result<T>> RunAsync(IRequest<T> request, CancellationToken cancellationToken)
    {
        var typed = (TRequest)request;

        #region 1. Guards

        foreach (var guard in guards)
            if (await guard.CheckAsync(typed, cancellationToken) is { } refused)
                return refused;

        #endregion

        #region 2. Validation

        var problems = validators.SelectMany(v => v.Validate(typed)).ToList();
        if (problems.Count > 0) return Error.Validation("Validation.Failed", string.Join(" ", problems));

        #endregion

        #region 3. Handler (inside a transaction for commands)

        var transactional = request is ICommand<T> && !RequestTraits.Of(typeof(TRequest)).NonTransactional;
        if (!transactional) return await handler.HandleAsync(typed, cancellationToken);

        await unitOfWork.BeginAsync(cancellationToken);
        try
        {
            var result = await handler.HandleAsync(typed, cancellationToken);

            if (result.IsSuccess) await unitOfWork.CommitAsync(cancellationToken);
            else await unitOfWork.RollbackAsync(cancellationToken);

            return result;
        }
        catch
        {
            await unitOfWork.RollbackAsync(cancellationToken);
            throw;
        }

        #endregion
    }
}

#endregion
