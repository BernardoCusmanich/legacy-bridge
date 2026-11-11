using System.Security.Cryptography;
using System.Text;

namespace LegacyBridge.Core.Delivery;

public static class WebhookSignature
{
    public const string EventIdHeader = "X-Bridge-Event-Id";
    public const string TimestampHeader = "X-Bridge-Timestamp";
    public const string SignatureHeader = "X-Bridge-Signature";
    public const string Prefix = "sha256=";

    /// <summary><c>sha256=hex(HMAC-SHA256(secret, "{timestamp}.{rawBody}"))</c></summary>
    public static string Compute(string secret, long unixTimestamp, string rawBody)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var message = Encoding.UTF8.GetBytes($"{unixTimestamp}.{rawBody}");
        var hash = HMACSHA256.HashData(key, message);
        return Prefix + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
