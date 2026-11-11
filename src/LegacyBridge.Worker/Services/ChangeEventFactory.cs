using LegacyBridge.Core.Abstractions;
using LegacyBridge.Core.Configuration;
using LegacyBridge.Core.Models;
using Microsoft.Extensions.Options;

namespace LegacyBridge.Worker.Services;

/// <summary>Transforma entradas do changelog em eventos prontos para a outbox.</summary>
public sealed class ChangeEventFactory
{
    private readonly IChangeSource _source;
    private readonly ILogger<ChangeEventFactory> _logger;
    private readonly string _sourceName;
    private readonly TimeZoneInfo _serverTimeZone;
    private readonly Dictionary<string, TableMapping> _tables;

    public ChangeEventFactory(IChangeSource source, IOptions<BridgeOptions> options, ILogger<ChangeEventFactory> logger)
    {
        _source = source;
        _logger = logger;
        var o = options.Value;
        _sourceName = o.SourceName;
        _serverTimeZone = TimeZoneInfo.FindSystemTimeZoneById(o.Firebird.ServerTimeZone);
        _tables = o.Tables.ToDictionary(t => t.Table, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Retorna <c>null</c> quando a entrada deve ser ignorada (tabela não mapeada etc.).</summary>
    public async Task<OutboxMessage?> CreateAsync(ChangeLogEntry entry, CancellationToken ct)
    {
        if (!_tables.TryGetValue(entry.Table, out var mapping))
        {
            _logger.LogWarning("Changelog {ChangelogId}: tabela {Table} não está em Bridge:Tables; ignorado", entry.Id, entry.Table);
            return null;
        }
        if (entry.Operation is not ('I' or 'U' or 'D'))
        {
            _logger.LogWarning("Changelog {ChangelogId}: operação desconhecida {Operation}; ignorado", entry.Id, entry.Operation);
            return null;
        }

        // Para I/U carregamos o estado atual da linha. Se ela já foi apagada, data = null.
        var data = entry.Operation == 'D'
            ? null
            : await _source.LoadRowAsync(mapping.Table, mapping.KeyColumn, entry.RecordId, ct);

        var evt = new BridgeEvent
        {
            EventId = UuidV7.NewId(),
            OccurredAt = ToUtc(entry.CreatedAt),
            Source = _sourceName,
            Entity = mapping.Entity,
            Operation = Operations.FromCode(entry.Operation),
            EntityId = entry.RecordId,
            Data = data,
        };
        return new OutboxMessage(evt.EventId, entry.Id, EventSerializer.Serialize(evt));
    }

    internal DateTime ToUtc(DateTime serverLocal)
    {
        var unspecified = DateTime.SpecifyKind(serverLocal, DateTimeKind.Unspecified);
        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(unspecified, _serverTimeZone);
        }
        catch (ArgumentException)
        {
            // Horário inexistente (pulo de horário de verão): usa o offset base.
            return DateTime.SpecifyKind(unspecified - _serverTimeZone.BaseUtcOffset, DateTimeKind.Utc);
        }
    }
}
