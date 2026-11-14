using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RxSuite.Api.Common;
using RxSuite.Application.Abstractions;
using RxSuite.Application.Features.Identity;
using RxSuite.Application.Features.Platform;

namespace RxSuite.Api.Controllers;

#region Public: pricing and sign-up (BRD 8.1)

/// <summary>The public pages: plans and the sign-up form. No login needed.</summary>
[AllowAnonymous]
[Route("api/public")]
public sealed class OnboardingController(IDispatcher dispatcher) : ApiControllerBase(dispatcher)
{
    /// <summary>SUB-03: the plans and their prices.</summary>
    [HttpGet("plans")]
    public Task<ActionResult<IReadOnlyList<PlanDto>>> GetPlans(CancellationToken ct) => Send(new GetPlansQuery(), ct);

    /// <summary>SUB-01 / SUB-04: creates the tenant, its first branch and its owner, on a 14-day trial.</summary>
    [HttpPost("sign-up")]
    public Task<ActionResult<SignUpResult>> SignUp([FromBody] SignUpCommand command, CancellationToken ct) =>
        SendCreated(command, ct);
}

#endregion

#region Login (BRD 8.2)

[Route("api/auth")]
public sealed class AuthController(IDispatcher dispatcher) : ApiControllerBase(dispatcher)
{
    /// <summary>
    /// USR-04: log in with mobile or e-mail and a password. On success the response body is the
    /// access token ({ tokenType, accessToken, expiresIn, refreshToken }).
    /// </summary>
    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginCommand command, CancellationToken ct)
    {
        var result = await Dispatcher.SendAsync(command, ct);

        return result.IsSuccess
            ? SignIn(TokenClaims.ToPrincipal(result.Value), BearerTokenDefaults.AuthenticationScheme)
            : ToProblem(result.Error);
    }
}

#endregion

#region Subscription and branches (BRD 8.1)

[Route("api/subscription")]
public sealed class SubscriptionController(IDispatcher dispatcher) : ApiControllerBase(dispatcher)
{
    /// <summary>SUB-11: the billing page.</summary>
    [HttpGet]
    public Task<ActionResult<SubscriptionDto>> Get(CancellationToken ct) => Send(new GetSubscriptionQuery(), ct);
}

[Route("api/branches")]
public sealed class BranchesController(IDispatcher dispatcher) : ApiControllerBase(dispatcher)
{
    [HttpGet]
    public Task<ActionResult<IReadOnlyList<BranchDto>>> GetAll(CancellationToken ct) => Send(new GetBranchesQuery(), ct);

    /// <summary>Adds a branch or warehouse, within the plan limit (SUB-12).</summary>
    [HttpPost]
    public Task<ActionResult<int>> Add([FromBody] AddBranchCommand command, CancellationToken ct) => SendCreated(command, ct);
}

#endregion

#region Payment gateway webhook (SUB-06)

/// <summary>
/// Called by the payment gateway (Paymob / Fawry) after a subscription payment succeeds.
/// There is no user here, so the caller proves itself with a shared secret header.
/// The real gateways sign their callbacks (HMAC); replace this check with theirs when integrating.
/// </summary>
[AllowAnonymous]
[Route("api/webhooks")]
public sealed class WebhooksController(IDispatcher dispatcher, IConfiguration configuration) : ApiControllerBase(dispatcher)
{
    public const string SecretHeader = "X-Webhook-Secret";

    [HttpPost("subscription-payment")]
    public async Task<ActionResult<DateOnly>> SubscriptionPayment(
        [FromBody] RecordSubscriptionPaymentCommand command, CancellationToken ct) =>
        IsTrustedCaller() ? await Send(command, ct) : Unauthorized();

    /// <summary>Constant-time comparison; an unset secret refuses every call.</summary>
    private bool IsTrustedCaller()
    {
        var expected = configuration["Webhooks:PaymentSecret"];
        var provided = Request.Headers[SecretHeader].ToString();

        return !string.IsNullOrEmpty(expected)
               && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(provided));
    }
}

#endregion
