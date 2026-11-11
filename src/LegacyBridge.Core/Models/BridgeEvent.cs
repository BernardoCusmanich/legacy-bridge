namespace LegacyBridge.Core.Models;

/// <summary>Evento enviado ao webhook. Serializado em camelCase.</summary>
public sealed record BridgeEvent
{
    public required string EventId { get; init; }

    /// <summary>Momento da mudança, em UTC.</summary>
    public required DateTime OccurredAt { get; init; }

    public required string Source { get; init; }
    public required string Entity { get; init; }
    public required string Operation { get; init; }
    public required int EntityId { get; init; }

    /// <summary>Colunas da linha em camelCase; <c>null</c> em deletes ou se a linha não existir mais.</summary>
    public IReadOnlyDictionary<string, object?>? Data { get; init; }
}
