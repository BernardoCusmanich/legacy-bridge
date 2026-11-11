using LegacyBridge.Core.Abstractions;
using LegacyBridge.Core.Configuration;
using LegacyBridge.Core.Models;
using Microsoft.Extensions.Options;

namespace LegacyBridge.Worker.Services;

/// <summary>Lê o changelog do Firebird e grava eventos na outbox junto com o checkpoint.</summary>
public sealed class PollerService(
    IChangeSource source,
    IOutboxStore store,
    ChangeEventFactory factory,
    OutboxSignal signal,
    IOptions<BridgeOptions> options,
    ILogger<PollerService> logger) : BackgroundService
{
    private readonly BridgeOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Poller iniciado: intervalo {Interval}s, lote {BatchSize}, {TableCount} tabelas",
            _options.PollingIntervalSeconds, _options.BatchSize, _options.Tables.Count);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.PollingIntervalSeconds));
        try
        {
            do
            {
                try
                {
                    await DrainAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Firebird fora do ar, rede etc.: tenta de novo no próximo ciclo.
                    logger.LogError(ex, "Falha no polling; nova tentativa em {Interval}s", _options.PollingIntervalSeconds);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        logger.LogInformation("Poller parado");
    }

    /// <summary>Consome lotes até o changelog esvaziar.</summary>
    private async Task DrainAsync(CancellationToken ct)
    {
        var lastId = await store.GetCheckpointAsync(_options.SourceName, ct);

        while (!ct.IsCancellationRequested)
        {
            var changes = await source.ReadChangesAsync(lastId, _options.BatchSize, ct);
            if (changes.Count == 0) return;

            var messages = new List<OutboxMessage>(changes.Count);
            foreach (var change in changes)
            {
                if (await factory.CreateAsync(change, ct) is { } message)
                    messages.Add(message);
            }

            var newCheckpoint = changes[^1].Id;
            var inserted = await store.EnqueueAsync(_options.SourceName, messages, newCheckpoint, ct);
            logger.LogInformation("Changelog {FromId}..{ToId}: {Inserted} eventos na outbox", changes[0].Id, newCheckpoint, inserted);

            if (inserted > 0) signal.Notify();
            lastId = newCheckpoint;

            if (changes.Count < _options.BatchSize) return;
        }
    }
}
