using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LegacyBridge.Core.Abstractions;
using LegacyBridge.Core.Configuration;
using LegacyBridge.Core.Delivery;
using Microsoft.Extensions.Options;
using Polly.Timeout;

namespace LegacyBridge.Infrastructure.Http;

public sealed class HttpWebhookSender(HttpClient http, IOptions<BridgeOptions> options, TimeProvider clock) : IWebhookSender
{
    private const int MaxErrorBodyChars = 300;
    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json");

    private readonly WebhookOptions _webhook = options.Value.Webhook;

    public async Task<DeliveryResult> SendAsync(string eventId, string payloadJson, CancellationToken ct)
    {
        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds();
        var body = Encoding.UTF8.GetBytes(payloadJson);

        using var request = new HttpRequestMessage(HttpMethod.Post, _webhook.Url);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = JsonContentType;
        request.Headers.Add(WebhookSignature.EventIdHeader, eventId);
        request.Headers.Add(WebhookSignature.TimestampHeader, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add(WebhookSignature.SignatureHeader, WebhookSignature.Compute(_webhook.Secret, timestamp, payloadJson));

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;
            var outcome = DeliveryResult.Classify(status);

            if (outcome == DeliveryOutcome.Delivered)
                return new DeliveryResult(outcome, status);

            var snippet = await ReadSnippetAsync(response, ct);
            var error = $"HTTP {status} {response.ReasonPhrase}{(snippet.Length > 0 ? ": " + snippet : "")}";
            return new DeliveryResult(outcome, status, error, GetRetryAfter(response));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TimeoutRejectedException or TaskCanceledException)
        {
            return new DeliveryResult(DeliveryOutcome.TransientFailure, null, $"Timeout após {_webhook.TimeoutSeconds}s");
        }
        catch (HttpRequestException ex)
        {
            return new DeliveryResult(DeliveryOutcome.TransientFailure, null, $"Erro de rede: {ex.HttpRequestError} {ex.Message}");
        }
    }

    private TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable))
            return null;

        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
            return delta;
        if (header?.Date is { } date)
        {
            var wait = date - clock.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    private static async Task<string> ReadSnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            text = text.ReplaceLineEndings(" ").Trim();
            return text.Length <= MaxErrorBodyChars ? text : text[..MaxErrorBodyChars] + "…";
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return "";
        }
    }
}
