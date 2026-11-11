using LegacyBridge.Core.Configuration;
using LegacyBridge.Infrastructure;
using LegacyBridge.Worker.Services;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

// Como Windows Service o diretório atual é C:\Windows\System32; por isso tudo que é
// relativo (appsettings, logs, SQLite) é resolvido a partir da pasta do executável.
var contentRoot = AppContext.BaseDirectory;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning) // não loga URLs/headers
    .MinimumLevel.Override("Polly", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(contentRoot, "logs", "bridge-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = contentRoot,
    });

    // No-op fora de um Windows Service; no Linux/Docker roda como console normal.
    builder.Services.AddWindowsService(o => o.ServiceName = "LegacyBridge");
    builder.Services.AddSerilog();

    builder.Services.AddSingleton<IValidateOptions<BridgeOptions>, BridgeOptionsValidator>();
    builder.Services.AddOptions<BridgeOptions>()
        .Bind(builder.Configuration.GetSection(BridgeOptions.SectionName))
        .ValidateOnStart();

    // Dá tempo para o envio em andamento terminar (timeout do webhook + folga).
    builder.Services.AddOptions<HostOptions>()
        .Configure<IOptions<BridgeOptions>>((host, bridge) =>
            host.ShutdownTimeout = TimeSpan.FromSeconds(bridge.Value.Webhook.TimeoutSeconds + 15));

    builder.Services.AddBridgeInfrastructure(builder.Environment.ContentRootPath);
    builder.Services.AddSingleton<OutboxSignal>();
    builder.Services.AddSingleton<ChangeEventFactory>();

    builder.Services.AddHostedService<StorageInitializer>(); // precisa vir antes dos dois abaixo
    builder.Services.AddHostedService<PollerService>();
    builder.Services.AddHostedService<DispatcherService>();

    var host = builder.Build();
    await host.RunAsync();
    return 0;
}
catch (OptionsValidationException ex)
{
    foreach (var failure in ex.Failures)
        Log.Fatal("Configuração inválida: {Failure}", failure);
    return 2;
}
catch (Exception ex)
{
    Log.Fatal(ex, "O bridge parou por um erro fatal");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
