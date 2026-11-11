using System.Text.Json;
using System.Text.Json.Serialization;

namespace LegacyBridge.Core.Models;

public static class EventSerializer
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public static string Serialize(BridgeEvent evt) => JsonSerializer.Serialize(evt, Options);

    /// <summary>Lê só os campos usados em log, sem materializar <c>data</c>.</summary>
    public static EventSummary ReadSummary(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new EventSummary(
            root.TryGetProperty("entity", out var e) ? e.GetString() ?? "?" : "?",
            root.TryGetProperty("entityId", out var id) && id.TryGetInt32(out var v) ? v : 0,
            root.TryGetProperty("operation", out var op) ? op.GetString() ?? "?" : "?");
    }
}

public readonly record struct EventSummary(string Entity, int EntityId, string Operation);
