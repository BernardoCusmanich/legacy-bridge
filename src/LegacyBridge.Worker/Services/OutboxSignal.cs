using System.Threading.Channels;

namespace LegacyBridge.Worker.Services;

/// <summary>Acorda o Dispatcher assim que o Poller grava algo na outbox.</summary>
public sealed class OutboxSignal
{
    private readonly Channel<byte> _channel = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => _channel.Writer.TryWrite(0);

    /// <summary>Espera um aviso ou o timeout, o que vier primeiro.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // timeout: segue o loop para pegar retries que venceram
        }
    }
}
