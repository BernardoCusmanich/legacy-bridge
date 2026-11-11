using LegacyBridge.Core.Delivery;

namespace LegacyBridge.Core.Abstractions;

public interface IWebhookSender
{
    /// <summary>Envia o corpo já serializado. Nunca lança por falha HTTP/rede: devolve o resultado classificado.</summary>
    Task<DeliveryResult> SendAsync(string eventId, string payloadJson, CancellationToken ct);
}
