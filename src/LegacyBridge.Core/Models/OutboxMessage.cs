namespace LegacyBridge.Core.Models;

/// <summary>Evento serializado pronto para entrar na outbox.</summary>
public sealed record OutboxMessage(string EventId, long ChangelogId, string PayloadJson);

/// <summary>Item pendente lido da outbox para envio.</summary>
public sealed record OutboxItem(string EventId, long ChangelogId, string PayloadJson, int Attempts);
