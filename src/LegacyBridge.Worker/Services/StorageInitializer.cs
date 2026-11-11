using LegacyBridge.Core.Abstractions;

namespace LegacyBridge.Worker.Services;

/// <summary>Cria as tabelas SQLite antes do Poller e do Dispatcher começarem.</summary>
public sealed class StorageInitializer(IOutboxStore store, ILogger<StorageInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await store.InitializeAsync(cancellationToken);
        logger.LogInformation("Armazenamento local pronto");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
