using LegacyBridge.Core.Abstractions;
using LegacyBridge.Core.Configuration;
using LegacyBridge.Infrastructure.Firebird;
using LegacyBridge.Infrastructure.Http;
using LegacyBridge.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Polly;

namespace LegacyBridge.Infrastructure;

public static class DependencyInjection
{
    /// <param name="contentRoot">Base para resolver caminhos relativos (ex.: SqlitePath).</param>
    public static IServiceCollection AddBridgeInfrastructure(this IServiceCollection services, string contentRoot)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IChangeSource, FirebirdChangeSource>();
        services.AddSingleton<IOutboxStore>(sp =>
        {
            var path = sp.GetRequiredService<IOptions<BridgeOptions>>().Value.Storage.SqlitePath;
            return new SqliteOutboxStore(Path.Combine(contentRoot, path));
        });

        // Sem retry no HttpClient de propósito: as novas tentativas são duráveis e
        // ficam na outbox (sobrevivem a restart). Aqui só aplicamos o timeout por envio.
        services.AddHttpClient<IWebhookSender, HttpWebhookSender>((sp, client) =>
            {
                var webhook = sp.GetRequiredService<IOptions<BridgeOptions>>().Value.Webhook;
                client.Timeout = TimeSpan.FromSeconds(webhook.TimeoutSeconds + 5); // rede de segurança
                client.DefaultRequestHeaders.UserAgent.ParseAdd("legacy-bridge/1.0");
            })
            .AddResilienceHandler("webhook", (pipeline, context) =>
            {
                var webhook = context.ServiceProvider.GetRequiredService<IOptions<BridgeOptions>>().Value.Webhook;
                pipeline.AddTimeout(TimeSpan.FromSeconds(webhook.TimeoutSeconds));
            });

        return services;
    }
}
