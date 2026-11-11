using System.Text;

namespace LegacyBridge.Core.Models;

public static class ColumnNaming
{
    /// <summary>CLIENTE_ID → clienteId, ATUALIZADO_EM → atualizadoEm, ID → id.</summary>
    public static string ToCamelCase(string column)
    {
        var parts = column.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return column.ToLowerInvariant();

        var sb = new StringBuilder(column.Length);
        sb.Append(parts[0].ToLowerInvariant());
        for (var i = 1; i < parts.Length; i++)
        {
            var p = parts[i].ToLowerInvariant();
            sb.Append(char.ToUpperInvariant(p[0])).Append(p.AsSpan(1));
        }
        return sb.ToString();
    }
}
