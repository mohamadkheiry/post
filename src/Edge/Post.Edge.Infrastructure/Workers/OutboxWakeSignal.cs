using System.Threading.Channels;

namespace Post.Edge.Infrastructure.Workers;

internal interface IOutboxWakeSignal
{
    void Notify();
    Task WaitAsync(TimeSpan pollingFallback, CancellationToken cancellationToken);
}

internal sealed class OutboxWakeSignal : IOutboxWakeSignal
{
    private readonly Channel<byte> _channel = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    public void Notify() => _channel.Writer.TryWrite(0);

    public async Task WaitAsync(TimeSpan pollingFallback, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollingFallback, TimeSpan.Zero);
        using var timeout = new CancellationTokenSource(pollingFallback);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await _channel.Reader.ReadAsync(linked.Token);
            while (_channel.Reader.TryRead(out _)) { }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            // The periodic fallback deliberately wakes an idle durable worker.
        }
    }
}
