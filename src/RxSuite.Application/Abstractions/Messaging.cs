using RxSuite.Domain.Common;

namespace RxSuite.Application.Abstractions;

// CQRS in its light form: a COMMAND changes state, a QUERY only reads. Each use case is one
// request record plus one small handler class (Single Responsibility).

#region Requests and handlers

/// <summary>A use-case request that produces <typeparamref name="T"/> or an error.</summary>
public interface IRequest<T>;

/// <summary>A request that changes state. Runs inside a database transaction.</summary>
public interface ICommand<T> : IRequest<T>;

/// <summary>A request that only reads.</summary>
public interface IQuery<T> : IRequest<T>;

/// <summary>Handles exactly one request type.</summary>
public interface IRequestHandler<in TRequest, T> where TRequest : IRequest<T>
{
    Task<Result<T>> HandleAsync(TRequest request, CancellationToken cancellationToken);
}

#endregion

#region Dispatcher (design pattern: Mediator)

/// <summary>
/// MEDIATOR. Controllers and background jobs send a request here and never reference a handler.
/// The dispatcher runs the request through the pipeline (guards, validation, transaction).
/// </summary>
public interface IDispatcher
{
    Task<Result<T>> SendAsync<T>(IRequest<T> request, CancellationToken cancellationToken);
}

#endregion

#region Request markers read by the pipeline

/// <summary>The caller must hold this permission (BRD section 9).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RequiresPermissionAttribute(string permission) : Attribute
{
    public string Permission { get; } = permission;
}

/// <summary>
/// The request runs without a logged-in user: public endpoints (sign-up, login, plans),
/// the payment webhook and background jobs. Never put it on anything else.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class NoUserRequiredAttribute : Attribute;

/// <summary>The command must save its changes even when it returns a failure (a failed login is counted).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class NonTransactionalAttribute : Attribute;

#endregion

#region Validation

/// <summary>
/// Checks the shape of a request (required fields, formats) before the use case runs.
/// Rules that need data stay in the handler; invariants stay in the domain.
/// </summary>
public interface IValidator<in T>
{
    /// <summary>One message per problem; an empty list means the request is valid.</summary>
    IReadOnlyList<string> Validate(T request);
}

#endregion
