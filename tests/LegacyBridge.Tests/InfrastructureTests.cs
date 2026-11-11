using System.Net;
using LegacyBridge.Core.Abstractions;
using LegacyBridge.Core.Configuration;
using LegacyBridge.Core.Delivery;
using LegacyBridge.Core.Models;
using LegacyBridge.Infrastructure.Http;
using LegacyBridge.Infrastructure.Storage;
using LegacyBridge.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LegacyBridge.Tests;

public sealed class SqliteOutboxStoreTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lb-tests-" + Guid.NewGuid().ToString("N"));
    private SqliteOutboxStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new SqliteOutboxStore(Path.Combine(_dir, "nested", "bridge.db"));
        await _store.InitializeAsync(default);
        await _store.InitializeAsync(default); // idempotente
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Checkpoint_comeca_em_zero_e_avanca_junto_com_a_outbox()
    {
        Assert.Equal(0, await _store.GetCheckpointAsync("erp", default));

        var inserted = await _store.EnqueueAsync("erp", [new("e1", 1, "{}"), new("e2", 2, "{}")], 2, default);

        Assert.Equal(2, inserted);
        Assert.Equal(2, await _store.GetCheckpointAsync("erp", default));
    }

    [Fact]
    public async Task Mesmo_changelog_id_nao_duplica()
    {
        await _store.EnqueueAsync("erp", [new("e1", 10, "{}")], 10, default);
        var inserted = await _store.EnqueueAsync("erp", [new("e1-bis", 10, "{}")], 10, default);

        Assert.Equal(0, inserted);
        Assert.Single(await _store.FetchDueAsync(100, DateTime.UtcNow, default));
    }

    [Fact]
    public async Task Checkpoint_nunca_retrocede()
    {
        await _store.EnqueueAsync("erp", [], 50, default);
        await _store.EnqueueAsync("erp", [], 20, default);
        Assert.Equal(50, await _store.GetCheckpointAsync("erp", default));
    }

    [Fact]
    public async Task FetchDue_respeita_status_next_attempt_e_ordem()
    {
        await _store.EnqueueAsync("erp", [new("c", 3, "{}"), new("a", 1, "{}"), new("b", 2, "{}"), new("d", 4, "{}")], 4, default);
        var now = DateTime.UtcNow;

        await _store.MarkSentAsync("a", 1, default);
        await _store.MarkDeadAsync("d", 1, "HTTP 400", default);
        await _store.ScheduleRetryAsync("b", 1, now.AddMinutes(5), "HTTP 503", default);

        var due = await _store.FetchDueAsync(100, now, default);
        Assert.Equal(new[] { "c" }, due.Select(i => i.EventId));

        var later = await _store.FetchDueAsync(100, now.AddMinutes(6), default);
        Assert.Equal(new[] { "b", "c" }, later.Select(i => i.EventId));
        Assert.Equal(1, later[0].Attempts);
    }

    [Fact]
    public async Task FetchDue_respeita_limite()
    {
        var msgs = Enumerable.Range(1, 10).Select(i => new OutboxMessage($"e{i}", i, "{}")).ToList();
        await _store.EnqueueAsync("erp", msgs, 10, default);
        Assert.Equal(3, (await _store.FetchDueAsync(3, DateTime.UtcNow, default)).Count);
    }
}

public class HttpWebhookSenderTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef";

    private static (HttpWebhookSender Sender, StubHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var options = Options.Create(new BridgeOptions
        {
            Webhook = { Url = "https://example.invalid/hook", Secret = Secret, TimeoutSeconds = 5 },
        });
        return (new HttpWebhookSender(new HttpClient(handler), options, TimeProvider.System), handler);
    }

    [Fact]
    public async Task Envia_headers_e_assinatura_validos()
    {
        var (sender, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var body = """{"eventId":"abc"}""";

        var result = await sender.SendAsync("abc", body, default);

        Assert.Equal(DeliveryOutcome.Delivered, result.Outcome);
        var req = handler.LastRequest!;
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("application/json", req.Content!.Headers.ContentType!.ToString());
        Assert.Equal("abc", req.Headers.GetValues("X-Bridge-Event-Id").Single());
        var ts = long.Parse(req.Headers.GetValues("X-Bridge-Timestamp").Single());
        Assert.InRange(ts, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5);
        Assert.Equal(WebhookSignature.Compute(Secret, ts, body), req.Headers.GetValues("X-Bridge-Signature").Single());
        Assert.Equal(body, handler.LastBody);
    }

    [Fact]
    public async Task Status_4xx_e_permanente()
    {
        var (sender, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("payload ruim") });
        var result = await sender.SendAsync("x", "{}", default);
        Assert.Equal(DeliveryOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(400, result.StatusCode);
        Assert.Contains("payload ruim", result.Error);
    }

    [Fact]
    public async Task Status_503_com_Retry_After_e_transitorio()
    {
        var (sender, _) = Create(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return r;
        });
        var result = await sender.SendAsync("x", "{}", default);
        Assert.Equal(DeliveryOutcome.TransientFailure, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
    }

    [Fact]
    public async Task Erro_de_rede_e_transitorio()
    {
        var (sender, _) = Create(_ => throw new HttpRequestException("conexão recusada"));
        var result = await sender.SendAsync("x", "{}", default);
        Assert.Equal(DeliveryOutcome.TransientFailure, result.Outcome);
        Assert.Null(result.StatusCode);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }
}

public class ChangeEventFactoryTests
{
    private static ChangeEventFactory Create(FakeSource source, string tz = "UTC") => new(
        source,
        Options.Create(new BridgeOptions
        {
            SourceName = "erp-demo",
            Firebird = { ServerTimeZone = tz },
            Tables = [new TableMapping { Table = "PEDIDOS", Entity = "pedido", KeyColumn = "ID" }],
        }),
        NullLogger<ChangeEventFactory>.Instance);

    [Fact]
    public async Task Insert_carrega_a_linha_e_monta_o_payload()
    {
        var source = new FakeSource { Row = new Dictionary<string, object?> { ["id"] = 5, ["status"] = "ABERTO" } };
        var msg = await Create(source).CreateAsync(new ChangeLogEntry(77, "PEDIDOS", 5, 'I', new DateTime(2026, 11, 11, 14, 0, 0)), default);

        Assert.NotNull(msg);
        Assert.Equal(77, msg.ChangelogId);
        Assert.Equal<(string, string, int)?>(("PEDIDOS", "ID", 5), source.LastLoad);
        Assert.Contains("\"operation\":\"created\"", msg.PayloadJson);
        Assert.Contains("\"occurredAt\":\"2026-11-11T14:00:00Z\"", msg.PayloadJson);
        Assert.Contains("\"data\":{\"id\":5,\"status\":\"ABERTO\"}", msg.PayloadJson);
        Assert.Contains($"\"eventId\":\"{msg.EventId}\"", msg.PayloadJson);
    }

    [Fact]
    public async Task Delete_nao_consulta_a_tabela_e_envia_data_null()
    {
        var source = new FakeSource();
        var msg = await Create(source).CreateAsync(new ChangeLogEntry(1, "PEDIDOS", 9, 'D', DateTime.Now), default);

        Assert.Null(source.LastLoad);
        Assert.Contains("\"data\":null", msg!.PayloadJson);
        Assert.Contains("\"operation\":\"deleted\"", msg.PayloadJson);
    }

    [Fact]
    public async Task Tabela_nao_mapeada_e_ignorada()
    {
        var msg = await Create(new FakeSource()).CreateAsync(new ChangeLogEntry(1, "OUTRA", 1, 'I', DateTime.Now), default);
        Assert.Null(msg);
    }

    [Fact]
    public void Converte_horario_do_servidor_para_utc()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"); // UTC-3, sem horário de verão desde 2019
        var factory = Create(new FakeSource(), tz.Id);
        var utc = factory.ToUtc(new DateTime(2026, 11, 11, 11, 0, 0));
        Assert.Equal(new DateTime(2026, 11, 11, 14, 0, 0, DateTimeKind.Utc), utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    private sealed class FakeSource : IChangeSource
    {
        public IReadOnlyDictionary<string, object?>? Row { get; init; }
        public (string, string, int)? LastLoad { get; private set; }

        public Task<IReadOnlyList<ChangeLogEntry>> ReadChangesAsync(long afterId, int batchSize, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChangeLogEntry>>([]);

        public Task<IReadOnlyDictionary<string, object?>?> LoadRowAsync(string table, string keyColumn, int id, CancellationToken ct)
        {
            LastLoad = (table, keyColumn, id);
            return Task.FromResult(Row);
        }
    }
}
