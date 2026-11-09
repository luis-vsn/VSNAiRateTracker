using System;
using System.Threading;
using System.Threading.Tasks;

namespace VSNAiRateTracker.Services;

public interface ISyncOrchestrator : IDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task SyncOnceAsync(CancellationToken cancellationToken = default);
    void OpenSettings();
    void Shutdown();
}
