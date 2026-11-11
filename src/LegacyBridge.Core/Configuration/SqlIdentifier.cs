using System.Text.RegularExpressions;

namespace LegacyBridge.Core.Configuration;

/// <summary>
/// Nomes de tabela e coluna não podem ser parametrizados em SQL. Por isso só aceitamos
/// identificadores vindos da configuração e que casem com um padrão estrito.
/// </summary>
public static partial class SqlIdentifier
{
    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,30}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsValid(string? identifier) =>
        identifier is not null && Pattern().IsMatch(identifier);

    /// <summary>Devolve o identificador se for válido; caso contrário lança.</summary>
    public static string Ensure(string? identifier) =>
        IsValid(identifier)
            ? identifier!
            : throw new ArgumentException($"Identificador SQL inválido: '{identifier}'.", nameof(identifier));
}
