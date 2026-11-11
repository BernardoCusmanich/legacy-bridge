using LegacyBridge.Core.Models;

namespace LegacyBridge.Core.Abstractions;

/// <summary>Origem das mudanças (o banco do ERP).</summary>
public interface IChangeSource
{
    /// <summary>Lê até <paramref name="batchSize"/> entradas do changelog com ID &gt; <paramref name="afterId"/>, em ordem.</summary>
    Task<IReadOnlyList<ChangeLogEntry>> ReadChangesAsync(long afterId, int batchSize, CancellationToken ct);

    /// <summary>Carrega a linha atual. Retorna <c>null</c> se ela não existir mais.</summary>
    Task<IReadOnlyDictionary<string, object?>?> LoadRowAsync(string table, string keyColumn, int id, CancellationToken ct);
}
