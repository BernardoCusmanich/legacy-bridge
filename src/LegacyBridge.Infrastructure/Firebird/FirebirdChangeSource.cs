using Dapper;
using FirebirdSql.Data.FirebirdClient;
using LegacyBridge.Core.Abstractions;
using LegacyBridge.Core.Configuration;
using LegacyBridge.Core.Models;
using Microsoft.Extensions.Options;

namespace LegacyBridge.Infrastructure.Firebird;

public sealed class FirebirdChangeSource : IChangeSource
{
    private readonly string _connectionString;
    private readonly string _changelogSql;

    public FirebirdChangeSource(IOptions<BridgeOptions> options)
    {
        var fb = options.Value.Firebird;
        _connectionString = fb.ConnectionString;

        var table = SqlIdentifier.Ensure(fb.ChangelogTable);
        _changelogSql =
            $"SELECT FIRST (@BatchSize) ID, TABELA, REGISTRO_ID AS REGISTROID, OPERACAO, CRIADO_EM AS CRIADOEM " +
            $"FROM {table} WHERE ID > @LastId ORDER BY ID";
    }

    public async Task<IReadOnlyList<ChangeLogEntry>> ReadChangesAsync(long afterId, int batchSize, CancellationToken ct)
    {
        await using var conn = new FbConnection(_connectionString);
        await conn.OpenAsync(ct);

        var rows = await conn.QueryAsync<ChangelogRow>(new CommandDefinition(
            _changelogSql,
            new { BatchSize = batchSize, LastId = afterId },
            cancellationToken: ct));

        return rows
            .Select(r => new ChangeLogEntry(
                r.Id,
                r.Tabela.Trim(),
                r.RegistroId,
                r.Operacao.Trim() is { Length: > 0 } op ? op[0] : '?',
                r.CriadoEm))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<string, object?>?> LoadRowAsync(string table, string keyColumn, int id, CancellationToken ct)
    {
        // Identificadores não podem ser parâmetros: validados contra regex estrita
        // (e vêm só da configuração). O valor da chave é sempre parametrizado.
        var sql = $"SELECT * FROM {SqlIdentifier.Ensure(table)} WHERE {SqlIdentifier.Ensure(keyColumn)} = @Id";

        await using var conn = new FbConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new FbCommand(sql, conn);
        cmd.Parameters.Add("@Id", FbDbType.Integer).Value = id;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var data = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var value = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
            if (value is string s) value = s.TrimEnd(); // CHAR(n) vem com padding
            data[ColumnNaming.ToCamelCase(reader.GetName(i))] = value;
        }
        return data;
    }

    // Firebird devolve aliases em maiúsculas; o Dapper casa nomes sem diferenciar caixa.
    private sealed class ChangelogRow
    {
        public long Id { get; init; }
        public string Tabela { get; init; } = "";
        public int RegistroId { get; init; }
        public string Operacao { get; init; } = "";
        public DateTime CriadoEm { get; init; }
    }
}
