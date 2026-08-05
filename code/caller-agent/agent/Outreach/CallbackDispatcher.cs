namespace CallerAgent.Outreach;

/// <summary>Retries persisted callback deliveries so transient failures and restarts do not lose results.</summary>
public sealed class CallbackDispatcher(
    OutreachService outreach,
    IConfiguration configuration,
    ILogger<CallbackDispatcher> logger) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(
        Math.Clamp(configuration.GetValue<int>("Outreach:CallbackDispatchSeconds", 10), 2, 300));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await outreach.DispatchDueCallbacksAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Callback dispatcher pass failed; retrying next interval");
            }

            try
            {
                await outreach.WaitForCallbackWorkAsync(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}