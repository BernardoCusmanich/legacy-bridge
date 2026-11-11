namespace LegacyBridge.Core.Models;

public static class Operations
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Deleted = "deleted";

    /// <summary>Converte o código do changelog (I/U/D) para o nome usado no payload.</summary>
    public static string FromCode(char code) => char.ToUpperInvariant(code) switch
    {
        'I' => Created,
        'U' => Updated,
        'D' => Deleted,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Operação desconhecida no changelog."),
    };
}
