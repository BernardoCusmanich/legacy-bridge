using LegacyBridge.Core.Models;

namespace LegacyBridge.Core.Abstractions;

/// <summary>Outbox local + checkpoint do poller.</summary>
public interface IOutboxStore
{
    Task InitializeAsync(CancellationToken ct);

    Task<long> GetCheckpointAsync(string source, CancellationToken ct);

    /// <summary>
    /// Insere os eventos (ignorando changelog_id repetido) e avança o checkpoint
    /// na mesma transação.
    /// </summary>
    /// <returns>Quantos eventos foram realmente inseridos.</returns>
    Task<int> EnqueueAsync(string source, IReadOnlyList<OutboxMessage> messages, long newCheckpoint, CancellationToken ct);

    /// <summary>Até <paramref name="limit"/> itens pendentes e vencidos, por changelog_id.</summary>
    Task<IReadOnlyList<OutboxItem>> FetchDueAsync(int limit, DateTime utcNow, CancellationToken ct);

    Task MarkSentAsync(string eventId, int attempts, CancellationToken ct);

    Task MarkDeadAsync(string eventId, int attempts, string error, CancellationToken ct);

    Task ScheduleRetryAsync(string eventId, int attempts, DateTime nextAttemptUtc, string error, CancellationToken ct);
}
