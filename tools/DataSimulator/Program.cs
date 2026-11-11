// Gera mudanças aleatórias no banco de demo para alimentar as triggers do bridge.
// Uso: DataSimulator [intervaloSegundos]   (padrão: 3)
// Conexão: env var FIREBIRD_CONNECTION_STRING.

using System.Globalization;
using System.Data.Common;
using Dapper;
using FirebirdSql.Data.FirebirdClient;

var intervalSeconds = args.Length > 0 && double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 3;
var connectionString = Environment.GetEnvironmentVariable("FIREBIRD_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Defina a variável de ambiente FIREBIRD_CONNECTION_STRING.");
    return 1;
}

var cts = new CancellationTokenSource(); // sem using: ProcessExit pode cancelar depois do fim
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

var sim = new Simulator(connectionString);
Log($"Simulador iniciado: uma mudança a cada {intervalSeconds}s");

while (!cts.IsCancellationRequested)
{
    try
    {
        Log(await sim.StepAsync());
    }
    catch (Exception ex) when (!cts.IsCancellationRequested)
    {
        // Ex.: Firebird ainda subindo. Só registra e tenta no próximo ciclo.
        Log($"erro: {ex.GetType().Name}: {ex.Message}");
    }

    try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cts.Token); }
    catch (OperationCanceledException) { break; }
}

Log("Simulador parado");
return 0;

static void Log(string message) =>
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

sealed class Simulator(string connectionString)
{
    private static readonly string[] FirstNames =
        ["Ana", "Bruno", "Carla", "Diego", "Elisa", "Fábio", "Gabriela", "Heitor", "Isabela", "João", "Lara", "Marcos", "Nina", "Otávio", "Paula", "Rafael"];
    private static readonly string[] LastNames =
        ["Almeida", "Barros", "Cardoso", "Duarte", "Esteves", "Farias", "Gomes", "Lima", "Moura", "Nogueira", "Pires", "Rocha", "Teixeira", "Vieira"];
    private static readonly string[] Items =
        ["Caneta azul", "Caderno 96 folhas", "Grampeador", "Pasta suspensa", "Clips nº 2", "Marca-texto", "Envelope A4", "Bloco adesivo", "Régua 30 cm", "Tesoura escolar"];
    private static readonly string[] Statuses = ["ABERTO", "PAGO", "ENVIADO", "ENTREGUE", "CANCELADO"];

    private readonly Random _rnd = new();

    public async Task<string> StepAsync()
    {
        await using var conn = new FbConnection(connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var table = _rnd.Next(3) switch { 0 => "CLIENTES", 1 => "PRODUTOS", _ => "PEDIDOS" };
        var roll = _rnd.NextDouble();
        var op = roll < 0.40 ? 'I' : roll < 0.85 ? 'U' : 'D';

        var result = (table, op) switch
        {
            ("CLIENTES", 'I') => await InsertClienteAsync(conn, tx),
            ("CLIENTES", 'U') => await UpdateClienteAsync(conn, tx),
            ("CLIENTES", _) => await DeleteClienteAsync(conn, tx),
            ("PRODUTOS", 'I') => await InsertProdutoAsync(conn, tx),
            ("PRODUTOS", 'U') => await UpdateProdutoAsync(conn, tx),
            ("PRODUTOS", _) => await DeleteAsync(conn, tx, "PRODUTOS"),
            (_, 'I') => await InsertPedidoAsync(conn, tx),
            (_, 'U') => await UpdatePedidoAsync(conn, tx),
            _ => await DeleteAsync(conn, tx, "PEDIDOS"),
        };

        await tx.CommitAsync();
        return result;
    }

    private async Task<string> InsertClienteAsync(FbConnection c, DbTransaction tx)
    {
        var nome = $"{Pick(FirstNames)} {Pick(LastNames)}";
        var id = await c.ExecuteScalarAsync<int>(
            "INSERT INTO CLIENTES (NOME, DOCUMENTO, EMAIL, ATUALIZADO_EM) VALUES (@nome, @doc, @email, LOCALTIMESTAMP) RETURNING ID",
            new { nome, doc = $"DEMO-{_rnd.Next(100000):D5}", email = Email(nome) }, tx);
        return $"INSERT CLIENTES #{id} ({nome})";
    }

    private async Task<string> UpdateClienteAsync(FbConnection c, DbTransaction tx)
    {
        var id = await RandomIdAsync(c, tx, "CLIENTES");
        if (id is null) return await InsertClienteAsync(c, tx);
        var nome = $"{Pick(FirstNames)} {Pick(LastNames)}";
        await c.ExecuteAsync(
            "UPDATE CLIENTES SET EMAIL = @email, ATUALIZADO_EM = LOCALTIMESTAMP WHERE ID = @id",
            new { email = Email(nome), id }, tx);
        return $"UPDATE CLIENTES #{id} (novo e-mail)";
    }

    private async Task<string> DeleteClienteAsync(FbConnection c, DbTransaction tx)
    {
        // Só apaga cliente sem pedidos, para não violar a FK.
        var id = await c.ExecuteScalarAsync<int?>(
            "SELECT FIRST 1 ID FROM CLIENTES C WHERE NOT EXISTS (SELECT 1 FROM PEDIDOS P WHERE P.CLIENTE_ID = C.ID) ORDER BY RAND()",
            transaction: tx);
        if (id is null) return await UpdateClienteAsync(c, tx);
        await c.ExecuteAsync("DELETE FROM CLIENTES WHERE ID = @id", new { id }, tx);
        return $"DELETE CLIENTES #{id}";
    }

    private async Task<string> InsertProdutoAsync(FbConnection c, DbTransaction tx)
    {
        var descricao = $"{Pick(Items)} lote {_rnd.Next(1, 99)}";
        var id = await c.ExecuteScalarAsync<int>(
            "INSERT INTO PRODUTOS (CODIGO, DESCRICAO, PRECO, ESTOQUE) VALUES (@codigo, @descricao, @preco, @estoque) RETURNING ID",
            new { codigo = $"SKU-{_rnd.Next(10000):D4}", descricao, preco = Money(2, 80), estoque = _rnd.Next(0, 500) }, tx);
        return $"INSERT PRODUTOS #{id} ({descricao})";
    }

    private async Task<string> UpdateProdutoAsync(FbConnection c, DbTransaction tx)
    {
        var id = await RandomIdAsync(c, tx, "PRODUTOS");
        if (id is null) return await InsertProdutoAsync(c, tx);
        var estoque = _rnd.Next(0, 500);
        await c.ExecuteAsync("UPDATE PRODUTOS SET ESTOQUE = @estoque, PRECO = @preco WHERE ID = @id",
            new { estoque, preco = Money(2, 80), id }, tx);
        return $"UPDATE PRODUTOS #{id} (estoque {estoque})";
    }

    private async Task<string> InsertPedidoAsync(FbConnection c, DbTransaction tx)
    {
        var clienteId = await RandomIdAsync(c, tx, "CLIENTES");
        if (clienteId is null) return await InsertClienteAsync(c, tx);
        var id = await c.ExecuteScalarAsync<int>(
            "INSERT INTO PEDIDOS (CLIENTE_ID, DATA, STATUS, TOTAL) VALUES (@clienteId, LOCALTIMESTAMP, 'ABERTO', @total) RETURNING ID",
            new { clienteId, total = Money(20, 2000) }, tx);
        return $"INSERT PEDIDOS #{id} (cliente #{clienteId})";
    }

    private async Task<string> UpdatePedidoAsync(FbConnection c, DbTransaction tx)
    {
        var id = await RandomIdAsync(c, tx, "PEDIDOS");
        if (id is null) return await InsertPedidoAsync(c, tx);
        var status = Pick(Statuses);
        await c.ExecuteAsync("UPDATE PEDIDOS SET STATUS = @status WHERE ID = @id", new { status, id }, tx);
        return $"UPDATE PEDIDOS #{id} (status {status})";
    }

    // "table" aqui é sempre uma constante deste arquivo, nunca entrada externa.
    private async Task<string> DeleteAsync(FbConnection c, DbTransaction tx, string table)
    {
        var id = await RandomIdAsync(c, tx, table);
        if (id is null) return $"DELETE {table}: nada para apagar";
        await c.ExecuteAsync($"DELETE FROM {table} WHERE ID = @id", new { id }, tx);
        return $"DELETE {table} #{id}";
    }

    private static Task<int?> RandomIdAsync(FbConnection c, DbTransaction tx, string table) =>
        c.ExecuteScalarAsync<int?>($"SELECT FIRST 1 ID FROM {table} ORDER BY RAND()", transaction: tx);

    private T Pick<T>(T[] values) => values[_rnd.Next(values.Length)];

    private decimal Money(int min, int max) => Math.Round((decimal)(min + _rnd.NextDouble() * (max - min)), 2);

    private static string Email(string nome) =>
        nome.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD)
            .Where(ch => ch is >= 'a' and <= 'z' or ' ')
            .Aggregate("", (acc, ch) => acc + (ch == ' ' ? '.' : ch)) + "@example.com";
}
