namespace LegacyBridge.Core.Delivery;

public enum DeliveryOutcome
{
    /// <summary>2xx.</summary>
    Delivered,

    /// <summary>4xx (exceto 408/429): não adianta tentar de novo.</summary>
    PermanentFailure,

    /// <summary>5xx, 408, 429, timeout ou erro de rede.</summary>
    TransientFailure,
}

/// <param name="StatusCode">Status HTTP, ou <c>null</c> se não houve resposta.</param>
/// <param name="Error">Descrição curta do erro, gravada em <c>last_error</c>.</param>
/// <param name="RetryAfter">Valor do header Retry-After (só em 429/503).</param>
public sealed record DeliveryResult(DeliveryOutcome Outcome, int? StatusCode, string? Error = null, TimeSpan? RetryAfter = null)
{
    public static DeliveryOutcome Classify(int statusCode) => statusCode switch
    {
        >= 200 and < 300 => DeliveryOutcome.Delivered,
        408 or 429 => DeliveryOutcome.TransientFailure,
        >= 400 and < 500 => DeliveryOutcome.PermanentFailure,
        _ => DeliveryOutcome.TransientFailure, // 5xx, e 1xx/3xx inesperados
    };
}
