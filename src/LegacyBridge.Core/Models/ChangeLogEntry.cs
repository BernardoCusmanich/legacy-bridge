namespace LegacyBridge.Core.Models;

/// <summary>Uma linha da tabela BRIDGE_CHANGELOG.</summary>
/// <param name="Id">ID sequencial do changelog (usado como checkpoint).</param>
/// <param name="Table">Tabela de origem (ex.: PEDIDOS).</param>
/// <param name="RecordId">Valor da chave da linha alterada.</param>
/// <param name="Operation">'I', 'U' ou 'D'.</param>
/// <param name="CreatedAt">CRIADO_EM no fuso do servidor (Kind = Unspecified).</param>
public sealed record ChangeLogEntry(long Id, string Table, int RecordId, char Operation, DateTime CreatedAt);
