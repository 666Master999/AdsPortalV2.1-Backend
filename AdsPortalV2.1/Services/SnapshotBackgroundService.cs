using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;

namespace AdsPortalV2.Services;

public class SnapshotBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private const int SnapshotThreshold = 500;
    private const int SnapshotLimit = 2000;

    public SnapshotBackgroundService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);
                using var scope = _scopeFactory.CreateScope();
                var writer = scope.ServiceProvider.GetRequiredService<DialogWriterService>();
                await writer.RunSnapshotPassAsync(SnapshotThreshold, SnapshotLimit);
            }
            catch (TaskCanceledException) { }
            catch { /* best-effort */ }
        }
    }
}
