namespace LegacyBridge.Core.Configuration;

/// <summary>Configuração da seção <c>Bridge</c> do appsettings.</summary>
public sealed class BridgeOptions
{
    public const string SectionName = "Bridge";

    public string SourceName { get; set; } = "erp-demo";
    public int PollingIntervalSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 100;
    public FirebirdOptions Firebird { get; set; } = new();
    public List<TableMapping> Tables { get; set; } = [];
    public WebhookOptions Webhook { get; set; } = new();
    public RetryOptions Retry { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
}

public sealed class FirebirdOptions
{
    public string ConnectionString { get; set; } = "";

    /// <summary>Nome da tabela de changelog alimentada pelas triggers.</summary>
    public string ChangelogTable { get; set; } = "BRIDGE_CHANGELOG";

    /// <summary>
    /// Fuso horário em que o servidor grava <c>CRIADO_EM</c> (TIMESTAMP sem fuso).
    /// Usado para converter <c>occurredAt</c> para UTC. Aceita IDs IANA ou Windows.
    /// </summary>
    public string ServerTimeZone { get; set; } = "UTC";
}

public sealed class TableMapping
{
    public string Table { get; set; } = "";
    public string Entity { get; set; } = "";
    public string KeyColumn { get; set; } = "ID";
}

public sealed class WebhookOptions
{
    public string Url { get; set; } = "";
    public string Secret { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 10;
}

public sealed class RetryOptions
{
    public int MaxAttempts { get; set; } = 10;
    public int BaseDelaySeconds { get; set; } = 2;
    public int MaxDelaySeconds { get; set; } = 300;
}

public sealed class StorageOptions
{
    public string SqlitePath { get; set; } = "data/bridge.db";
}
