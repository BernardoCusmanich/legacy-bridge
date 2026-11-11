// Receptor de demonstração. Mostra como um consumidor deve validar os eventos
// do legacy-bridge: assinatura HMAC, janela de timestamp e deduplicação por eventId.
// Não depende dos projetos do bridge de propósito: é o que um terceiro escreveria.

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var secret = builder.Configuration["WEBHOOK_SECRET"];
if (string.IsNullOrEmpty(secret) || secret.Length < 32)
{
    app.Logger.LogCritical("Defina WEBHOOK_SECRET com pelo menos 32 caracteres");
    return 1;
}
var secretKey = Encoding.UTF8.GetBytes(secret);

var failRate = double.TryParse(builder.Configuration["FAIL_RATE"], NumberStyles.Float, CultureInfo.InvariantCulture, out var fr)
    ? Math.Clamp(fr, 0, 1)
    : 0;
var tolerance = TimeSpan.FromMinutes(5);

var store = new EventStore(recentCapacity: 50, dedupCapacity: 10_000);

app.MapPost("/webhook", async (HttpRequest request, ILogger<Program> log) =>
{
    // 1. Corpo bruto: a assinatura é calculada sobre os bytes exatos recebidos.
    using var reader = new StreamReader(request.Body, Encoding.UTF8);
    var rawBody = await reader.ReadToEndAsync();

    var eventIdHeader = request.Headers["X-Bridge-Event-Id"].ToString();
    var timestampHeader = request.Headers["X-Bridge-Timestamp"].ToString();
    var signatureHeader = request.Headers["X-Bridge-Signature"].ToString();

    // 2. Timestamp dentro de ±5 min (protege contra replay de requisições antigas).
    if (!long.TryParse(timestampHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var ts))
        return Results.Problem("X-Bridge-Timestamp ausente ou inválido", statusCode: 401);
    var skew = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(ts);
    if (skew.Duration() > tolerance)
        return Results.Problem("Timestamp fora da janela de 5 minutos", statusCode: 401);

    // 3. Assinatura em tempo constante.
    var expected = "sha256=" + Convert.ToHexString(
        HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes($"{ts}.{rawBody}"))).ToLowerInvariant();
    if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signatureHeader)))
        return Results.Problem("Assinatura inválida", statusCode: 401);

    // Só depois de autenticar é que confiamos no conteúdo.
    JsonNode? payload;
    try { payload = JsonNode.Parse(rawBody); }
    catch (JsonException) { return Results.BadRequest(new { error = "JSON inválido" }); }

    string? eventId = null;
    if (payload?["eventId"] is JsonValue idValue) idValue.TryGetValue(out eventId);
    if (payload is null || string.IsNullOrEmpty(eventId) || eventId != eventIdHeader)
        return Results.BadRequest(new { error = "eventId ausente ou diferente do header" });

    // 4. Falha simulada para demonstrar o retry do bridge.
    if (failRate > 0 && Random.Shared.NextDouble() < failRate)
    {
        log.LogWarning("Falha simulada (FAIL_RATE={FailRate}) para {EventId}", failRate, eventId);
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    // 5. Idempotência: o bridge entrega at-least-once.
    if (!store.TryAdd(eventId, payload))
    {
        log.LogInformation("Evento {EventId} repetido; ignorado", eventId);
        return Results.Ok(new { status = "duplicate" });
    }

    log.LogInformation("Recebido {EventId}: {Entity}#{EntityId} {Operation}",
        eventId, payload["entity"]?.ToString(), payload["entityId"]?.ToString(), payload["operation"]?.ToString());
    return Results.Ok(new { status = "accepted" });
});

app.MapGet("/events", () => Results.Ok(store.Recent()));
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Logger.LogInformation("WebhookReceiver pronto (FAIL_RATE={FailRate})", failRate);
await app.RunAsync();
return 0;

/// <summary>Memória limitada: últimos N eventos para /events e um conjunto de IDs para dedup.</summary>
sealed class EventStore(int recentCapacity, int dedupCapacity)
{
    private readonly object _lock = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private readonly LinkedList<ReceivedEvent> _recent = new();

    public bool TryAdd(string eventId, JsonNode payload)
    {
        lock (_lock)
        {
            if (!_seen.Add(eventId)) return false;
            _seenOrder.Enqueue(eventId);
            if (_seenOrder.Count > dedupCapacity) _seen.Remove(_seenOrder.Dequeue());

            _recent.AddFirst(new ReceivedEvent(DateTimeOffset.UtcNow, payload));
            if (_recent.Count > recentCapacity) _recent.RemoveLast();
            return true;
        }
    }

    public IReadOnlyList<ReceivedEvent> Recent()
    {
        lock (_lock) return _recent.ToList();
    }
}

sealed record ReceivedEvent(DateTimeOffset ReceivedAt, JsonNode Event);
