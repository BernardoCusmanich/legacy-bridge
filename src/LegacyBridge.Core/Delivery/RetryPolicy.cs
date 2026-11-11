using LegacyBridge.Core.Configuration;

namespace LegacyBridge.Core.Delivery;

public readonly record struct RetryDecision(bool GiveUp, int Attempts, TimeSpan Delay);

/// <summary>Backoff exponencial com teto e jitter de 0–20%.</summary>
public sealed class RetryPolicy(RetryOptions options, Func<double>? random = null)
{
    private const double MaxJitter = 0.20;
    private readonly Func<double> _random = random ?? Random.Shared.NextDouble;

    /// <summary>
    /// Decide o que fazer após uma falha transitória.
    /// </summary>
    /// <param name="previousAttempts">Tentativas já registradas antes desta falha.</param>
    /// <param name="retryAfter">Header Retry-After, se o servidor enviou.</param>
    public RetryDecision OnTransientFailure(int previousAttempts, TimeSpan? retryAfter = null)
    {
        var attempts = previousAttempts + 1;
        if (attempts >= options.MaxAttempts)
            return new RetryDecision(GiveUp: true, attempts, TimeSpan.Zero);

        if (retryAfter is { } ra && ra > TimeSpan.Zero)
            return new RetryDecision(GiveUp: false, attempts, ra);

        // min(base * 2^attempts, max), calculado em double para não estourar.
        var seconds = Math.Min(options.BaseDelaySeconds * Math.Pow(2, attempts), options.MaxDelaySeconds);
        seconds *= 1 + _random() * MaxJitter;
        return new RetryDecision(GiveUp: false, attempts, TimeSpan.FromSeconds(seconds));
    }
}
