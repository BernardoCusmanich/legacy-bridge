using LegacyBridge.Core.Abstractions;
using LegacyBridge.Core.Configuration;
using LegacyBridge.Core.Delivery;
using LegacyBridge.Core.Models;
using Microsoft.Extensions.Options;

namespace LegacyBridge.Worker.Services;

/// <summary>Envia os itens pendentes da outbox e aplica a política de retry.</summary>
public sealed class DispatcherService(
    IOutboxStore store,
    IWebhookSender sender,
    OutboxSignal signal,
    TimeProvider clock,
    IOptions<BridgeOptions> options,
    ILogger<DispatcherService> logger) : BackgroundService
{
    private static readonly TimeSpan IdleWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ErrorWait = TimeSpan.FromSeconds(5);

    private readonly BridgeOptions _options = options.Value;
    private readonly RetryPolicy _retry = new(options.Value.Retry);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Dispatcher iniciado");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                IReadOnlyList<OutboxItem> due;
                try
                {
                    due = await store.FetchDueAsync(_options.BatchSize, clock.GetUtcNow().UtcDateTime, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Falha ao ler a outbox");
                    await Task.Delay(ErrorWait, stoppingToken);
                    continue;
                }

                if (due.Count == 0)
                {
                    await signal.WaitAsync(IdleWait, stoppingToken);
                    continue;
                }

                foreach (var item in due)
                {
                    // Shutdown gracioso: não começa um envio novo, mas o que já começou
                    // (DispatchAsync usa CancellationToken.None) termina e é persistido.
                    if (stoppingToken.IsCancellationRequested) break;
                    await DispatchAsync(item);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        logger.LogInformation("Dispatcher parado");
    }

    private async Task DispatchAsync(OutboxItem item)
    {
        var ct = CancellationToken.None; // limitado pelo timeout do HttpClient
        var attempt = item.Attempts + 1;
        var summary = SafeSummary(item.PayloadJson);

        try
        {
            var result = await sender.SendAsync(item.EventId, item.PayloadJson, ct);

            switch (result.Outcome)
            {
                case DeliveryOutcome.Delivered:
                    await store.MarkSentAsync(item.EventId, attempt, ct);
                    logger.LogInformation(
                        "Evento {EventId} {Entity}#{EntityId} ({Operation}) entregue: HTTP {StatusCode}, tentativa {Attempt}",
                        item.EventId, summary.Entity, summary.EntityId, summary.Operation, result.StatusCode, attempt);
                    break;

                case DeliveryOutcome.PermanentFailure:
                    await store.MarkDeadAsync(item.EventId, attempt, result.Error ?? "falha permanente", ct);
                    logger.LogError(
                        "Evento {EventId} {Entity}#{EntityId} rejeitado: HTTP {StatusCode}, tentativa {Attempt}; marcado como dead",
                        item.EventId, summary.Entity, summary.EntityId, result.StatusCode, attempt);
                    break;

                default:
                    await HandleTransientAsync(item, summary, result);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Falha inesperada (ex.: SQLite travado). O item continua pending e será reenviado:
            // entrega at-least-once, o receptor deduplica por eventId.
            logger.LogError(ex, "Erro ao processar o evento {EventId}", item.EventId);
        }
    }

    private async Task HandleTransientAsync(OutboxItem item, EventSummary summary, DeliveryResult result)
    {
        var decision = _retry.OnTransientFailure(item.Attempts, result.RetryAfter);
        var error = result.Error ?? "falha transitória";
        var status = result.StatusCode?.ToString() ?? "sem resposta";

        if (decision.GiveUp)
        {
            await store.MarkDeadAsync(item.EventId, decision.Attempts, error, CancellationToken.None);
            logger.LogError(
                "Evento {EventId} {Entity}#{EntityId} falhou ({Status}) na tentativa {Attempt} de {MaxAttempts}; marcado como dead",
                item.EventId, summary.Entity, summary.EntityId, status, decision.Attempts, _options.Retry.MaxAttempts);
            return;
        }

        var next = clock.GetUtcNow().UtcDateTime + decision.Delay;
        await store.ScheduleRetryAsync(item.EventId, decision.Attempts, next, error, CancellationToken.None);
        logger.LogWarning(
            "Evento {EventId} {Entity}#{EntityId} falhou ({Status}) na tentativa {Attempt}; nova tentativa em {DelaySeconds:F1}s",
            item.EventId, summary.Entity, summary.EntityId, status, decision.Attempts, decision.Delay.TotalSeconds);
    }

    private static EventSummary SafeSummary(string json)
    {
        try { return EventSerializer.ReadSummary(json); }
        catch (System.Text.Json.JsonException) { return new EventSummary("?", 0, "?"); }
    }
}
