using RxSuite.Application.Abstractions;
using RxSuite.Application.Features.Inventory;
using RxSuite.Application.Features.Platform;
using RxSuite.Application.Features.Tax;

namespace RxSuite.Api.BackgroundJobs;

// Work that no user triggers. Each run opens its own dependency-injection scope (its own
// database session) and sends an ordinary command through the dispatcher, so the rules live
// in the Application layer like everything else. Turn the jobs off with "Jobs:Enabled": false.

#region Base class (design pattern: Template Method)

/// <summary>
/// TEMPLATE METHOD. Owns the loop, the scope and the error handling; a job only says how often
/// it runs and what it does. One failed run is logged and the next run goes ahead.
/// </summary>
internal abstract class PeriodicJob(IServiceScopeFactory scopes, ILogger logger, TimeSpan interval) : BackgroundService
{
    protected abstract Task RunAsync(IDispatcher dispatcher, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await RunAsync(scope.ServiceProvider.GetRequiredService<IDispatcher>(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Background job {Job} failed", GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

#endregion

#region Nightly jobs

/// <summary>
/// Once a day (checked hourly, so a restart never skips a day):
/// - subscriptions: trial or paid period over -> Grace, grace over -> ReadOnly (BR-SUB-4);
/// - stock: expired batches go to quarantine (INV-08, AC-INV-1).
/// </summary>
internal sealed class NightlyJobsService(IServiceScopeFactory scopes, ILogger<NightlyJobsService> logger, IClock clock)
    : PeriodicJob(scopes, logger, TimeSpan.FromHours(1))
{
    private DateOnly? _lastRun;

    protected override async Task RunAsync(IDispatcher dispatcher, CancellationToken ct)
    {
        if (_lastRun == clock.Today) return;

        var tenants = await dispatcher.SendAsync(new RefreshSubscriptionStatusesCommand(), ct);
        var batches = await dispatcher.SendAsync(new QuarantineExpiredBatchesCommand(), ct);

        logger.LogInformation("Nightly jobs: {Tenants} tenant status change(s), {Batches} batch(es) quarantined",
            tenants.IsSuccess ? tenants.Value : 0, batches.IsSuccess ? batches.Value : 0);
        _lastRun = clock.Today;
    }
}

#endregion

#region ETA submission

/// <summary>Every 30 seconds: send the queued tax documents to ETA (ACC-03, ACC-07).</summary>
internal sealed class EtaSubmissionService(IServiceScopeFactory scopes, ILogger<EtaSubmissionService> logger)
    : PeriodicJob(scopes, logger, TimeSpan.FromSeconds(30))
{
    protected override Task RunAsync(IDispatcher dispatcher, CancellationToken ct) =>
        dispatcher.SendAsync(new SubmitQueuedEtaDocumentsCommand(), ct);
}

#endregion
