using LegacyBridge.Core.Configuration;
using LegacyBridge.Core.Delivery;
using LegacyBridge.Core.Models;

namespace LegacyBridge.Tests;

public class RetryPolicyTests
{
    private static readonly RetryOptions Options = new() { MaxAttempts = 5, BaseDelaySeconds = 2, MaxDelaySeconds = 20 };

    [Theory]
    [InlineData(0, 4)]   // 1ª falha: 2 * 2^1
    [InlineData(1, 8)]   // 2 * 2^2
    [InlineData(2, 16)]  // 2 * 2^3
    [InlineData(3, 20)]  // teto
    public void Backoff_exponencial_com_teto_sem_jitter(int previous, double expectedSeconds)
    {
        var policy = new RetryPolicy(Options, random: () => 0);
        var d = policy.OnTransientFailure(previous);
        Assert.False(d.GiveUp);
        Assert.Equal(previous + 1, d.Attempts);
        Assert.Equal(expectedSeconds, d.Delay.TotalSeconds, precision: 6);
    }

    [Fact]
    public void Jitter_fica_entre_0_e_20_por_cento()
    {
        var max = new RetryPolicy(Options, random: () => 0.999999).OnTransientFailure(0);
        Assert.InRange(max.Delay.TotalSeconds, 4, 4 * 1.2);
    }

    [Fact]
    public void Desiste_ao_atingir_MaxAttempts()
    {
        var d = new RetryPolicy(Options).OnTransientFailure(previousAttempts: 4);
        Assert.True(d.GiveUp);
        Assert.Equal(5, d.Attempts);
    }

    [Fact]
    public void Respeita_Retry_After()
    {
        var d = new RetryPolicy(Options, random: () => 0).OnTransientFailure(0, TimeSpan.FromSeconds(42));
        Assert.Equal(TimeSpan.FromSeconds(42), d.Delay);
    }
}

public class DeliveryClassificationTests
{
    [Theory]
    [InlineData(200, DeliveryOutcome.Delivered)]
    [InlineData(204, DeliveryOutcome.Delivered)]
    [InlineData(400, DeliveryOutcome.PermanentFailure)]
    [InlineData(401, DeliveryOutcome.PermanentFailure)]
    [InlineData(404, DeliveryOutcome.PermanentFailure)]
    [InlineData(408, DeliveryOutcome.TransientFailure)]
    [InlineData(429, DeliveryOutcome.TransientFailure)]
    [InlineData(500, DeliveryOutcome.TransientFailure)]
    [InlineData(503, DeliveryOutcome.TransientFailure)]
    public void Classifica_status(int status, DeliveryOutcome expected) =>
        Assert.Equal(expected, DeliveryResult.Classify(status));
}

public class SignatureTests
{
    [Fact]
    public void Hmac_sha256_de_timestamp_ponto_corpo()
    {
        // Vetor de referência gerado com Python: hmac.new(secret, b'1700000000.{"eventId":"x"}', sha256)
        var sig = WebhookSignature.Compute("0123456789abcdef0123456789abcdef", 1700000000, "{\"eventId\":\"x\"}");
        Assert.Equal("sha256=f8642c847f1cbe9659293ebce143773330c895fcfbb0485f568e8e922ebc2801", sig);
    }
}

public class SqlIdentifierTests
{
    [Theory]
    [InlineData("CLIENTES", true)]
    [InlineData("BRIDGE_CHANGELOG", true)]
    [InlineData("T1", true)]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ01234", true)]   // 31 chars
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ012345", false)] // 32 chars
    [InlineData("clientes", false)]
    [InlineData("1TABELA", false)]
    [InlineData("_X", false)]
    [InlineData("CLIENTES; DROP TABLE X", false)]
    [InlineData("CLIENTES--", false)]
    [InlineData("\"CLIENTES\"", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Valida_identificadores(string? id, bool valid) => Assert.Equal(valid, SqlIdentifier.IsValid(id));

    [Fact]
    public void Ensure_lanca_para_invalido() =>
        Assert.Throws<ArgumentException>(() => SqlIdentifier.Ensure("X Y"));
}

public class NamingAndIdTests
{
    [Theory]
    [InlineData("ID", "id")]
    [InlineData("CLIENTE_ID", "clienteId")]
    [InlineData("ATUALIZADO_EM", "atualizadoEm")]
    [InlineData("DESCRICAO", "descricao")]
    public void Converte_para_camelCase(string column, string expected) =>
        Assert.Equal(expected, ColumnNaming.ToCamelCase(column));

    [Fact]
    public void UuidV7_tem_versao_variante_e_ordem_temporal()
    {
        var a = UuidV7.NewId(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
        var b = UuidV7.NewId(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_001));

        Assert.Equal('7', a[14]);
        Assert.Contains(a[19], "89ab");
        Assert.True(string.CompareOrdinal(a, b) < 0);
        Assert.StartsWith("018bcfe5-6800", a); // 1_700_000_000_000 = 0x018BCFE56800
    }

    [Fact]
    public void Operacoes_I_U_D()
    {
        Assert.Equal("created", Operations.FromCode('I'));
        Assert.Equal("updated", Operations.FromCode('U'));
        Assert.Equal("deleted", Operations.FromCode('D'));
        Assert.Throws<ArgumentOutOfRangeException>(() => Operations.FromCode('X'));
    }

    [Fact]
    public void Serializa_payload_em_camelCase_com_data_em_utc()
    {
        var json = EventSerializer.Serialize(new BridgeEvent
        {
            EventId = "e1",
            OccurredAt = new DateTime(2026, 11, 11, 14, 0, 0, DateTimeKind.Utc),
            Source = "erp-demo",
            Entity = "pedido",
            Operation = "created",
            EntityId = 123,
            Data = new Dictionary<string, object?> { ["clienteId"] = 7, ["total"] = 10.5m },
        });

        Assert.Equal(
            """{"eventId":"e1","occurredAt":"2026-11-11T14:00:00Z","source":"erp-demo","entity":"pedido","operation":"created","entityId":123,"data":{"clienteId":7,"total":10.5}}""",
            json);
    }

    [Fact]
    public void Serializa_data_null_em_delete()
    {
        var json = EventSerializer.Serialize(new BridgeEvent
        {
            EventId = "e2", OccurredAt = DateTime.UtcNow, Source = "s", Entity = "cliente",
            Operation = "deleted", EntityId = 1, Data = null,
        });
        Assert.Contains("\"data\":null", json);
    }
}

public class OptionsValidatorTests
{
    private static BridgeOptions Valid() => new()
    {
        Firebird = { ConnectionString = "DataSource=x" },
        Tables = [new TableMapping { Table = "CLIENTES", Entity = "cliente", KeyColumn = "ID" }],
        Webhook = { Url = "https://example.invalid/hook", Secret = new string('s', 32) },
    };

    [Fact]
    public void Config_valida_passa() =>
        Assert.True(new BridgeOptionsValidator().Validate(null, Valid()).Succeeded);

    [Fact]
    public void Falha_sem_url_connection_string_ou_com_secret_curto()
    {
        var o = Valid();
        o.Webhook.Url = "";
        o.Firebird.ConnectionString = "";
        o.Webhook.Secret = "curto";

        var result = new BridgeOptionsValidator().Validate(null, o);
        Assert.True(result.Failed);
        var failures = string.Join("\n", result.Failures!);
        Assert.Contains("Webhook:Url", failures);
        Assert.Contains("ConnectionString", failures);
        Assert.Contains("Secret", failures);
        Assert.DoesNotContain("curto", failures); // nunca ecoa o segredo
    }

    [Fact]
    public void Falha_com_identificador_invalido()
    {
        var o = Valid();
        o.Tables[0].Table = "CLIENTES; DROP";
        Assert.True(new BridgeOptionsValidator().Validate(null, o).Failed);
    }
}
