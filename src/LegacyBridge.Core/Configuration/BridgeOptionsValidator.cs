using Microsoft.Extensions.Options;

namespace LegacyBridge.Core.Configuration;

/// <summary>Validação executada na inicialização (<c>ValidateOnStart</c>).</summary>
public sealed class BridgeOptionsValidator : IValidateOptions<BridgeOptions>
{
    public const int MinSecretLength = 32;

    public ValidateOptionsResult Validate(string? name, BridgeOptions o)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(o.SourceName))
            errors.Add("Bridge:SourceName é obrigatório.");
        if (o.PollingIntervalSeconds < 1)
            errors.Add("Bridge:PollingIntervalSeconds deve ser >= 1.");
        if (o.BatchSize is < 1 or > 10_000)
            errors.Add("Bridge:BatchSize deve estar entre 1 e 10000.");

        if (string.IsNullOrWhiteSpace(o.Firebird.ConnectionString))
            errors.Add("Bridge:Firebird:ConnectionString é obrigatório (use a env var Bridge__Firebird__ConnectionString ou user-secrets).");
        if (!SqlIdentifier.IsValid(o.Firebird.ChangelogTable))
            errors.Add($"Bridge:Firebird:ChangelogTable '{o.Firebird.ChangelogTable}' não é um identificador válido.");
        if (!TryFindTimeZone(o.Firebird.ServerTimeZone))
            errors.Add($"Bridge:Firebird:ServerTimeZone '{o.Firebird.ServerTimeZone}' não é um fuso horário conhecido.");

        if (o.Tables.Count == 0)
            errors.Add("Bridge:Tables precisa de pelo menos uma tabela.");
        for (var i = 0; i < o.Tables.Count; i++)
        {
            var t = o.Tables[i];
            if (!SqlIdentifier.IsValid(t.Table))
                errors.Add($"Bridge:Tables:{i}:Table '{t.Table}' não casa com ^[A-Z][A-Z0-9_]{{0,30}}$.");
            if (!SqlIdentifier.IsValid(t.KeyColumn))
                errors.Add($"Bridge:Tables:{i}:KeyColumn '{t.KeyColumn}' não casa com ^[A-Z][A-Z0-9_]{{0,30}}$.");
            if (string.IsNullOrWhiteSpace(t.Entity))
                errors.Add($"Bridge:Tables:{i}:Entity é obrigatório.");
        }
        var duplicated = o.Tables.GroupBy(t => t.Table, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicated.Count > 0)
            errors.Add($"Bridge:Tables tem tabelas repetidas: {string.Join(", ", duplicated)}.");

        if (!Uri.TryCreate(o.Webhook.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            errors.Add("Bridge:Webhook:Url é obrigatório e deve ser uma URL http(s) absoluta.");
        if (string.IsNullOrEmpty(o.Webhook.Secret) || o.Webhook.Secret.Length < MinSecretLength)
            errors.Add($"Bridge:Webhook:Secret deve ter pelo menos {MinSecretLength} caracteres (use a env var Bridge__Webhook__Secret ou user-secrets).");
        if (o.Webhook.TimeoutSeconds is < 1 or > 300)
            errors.Add("Bridge:Webhook:TimeoutSeconds deve estar entre 1 e 300.");

        if (o.Retry.MaxAttempts < 1)
            errors.Add("Bridge:Retry:MaxAttempts deve ser >= 1.");
        if (o.Retry.BaseDelaySeconds < 1)
            errors.Add("Bridge:Retry:BaseDelaySeconds deve ser >= 1.");
        if (o.Retry.MaxDelaySeconds < o.Retry.BaseDelaySeconds)
            errors.Add("Bridge:Retry:MaxDelaySeconds deve ser >= BaseDelaySeconds.");

        if (string.IsNullOrWhiteSpace(o.Storage.SqlitePath))
            errors.Add("Bridge:Storage:SqlitePath é obrigatório.");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static bool TryFindTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }
}
