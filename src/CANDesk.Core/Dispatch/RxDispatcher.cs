using System.Threading.Channels;
using CANDesk.Hal;

namespace CANDesk.Core.Dispatch;

public interface IFrameFilter { bool Matches(in CanFrame frame); }
public sealed record CanIdFilter(uint Id, uint Mask = uint.MaxValue) : IFrameFilter
{
    public bool Matches(in CanFrame frame) => (frame.Id & Mask) == (Id & Mask);
}
public interface IRxDispatcher : IAsyncDisposable
{
    event EventHandler<IReadOnlyList<CanFrame>>? FramesBatched;
    long DroppedFrameCount { get; }
    void AddFilter(IFrameFilter filter);
    Task StartAsync(IAsyncEnumerable<CanFrame> source, CancellationToken cancellationToken = default);
}
public sealed class RxDispatcher(TimeSpan? batchInterval = null, int capacity = 16_384) : IRxDispatcher
{
    private readonly Channel<CanFrame> _channel = Channel.CreateBounded<CanFrame>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly List<IFrameFilter> _filters = [];
    private CancellationTokenSource? _cts;
    private Task? _pump;
    private Task? _batcher;
    private long _dropped;
    private int _disposeState;
    public event EventHandler<IReadOnlyList<CanFrame>>? FramesBatched;
    public long DroppedFrameCount => Interlocked.Read(ref _dropped);
    public void AddFilter(IFrameFilter filter) => _filters.Add(filter ?? throw new ArgumentNullException(nameof(filter)));
    public Task StartAsync(IAsyncEnumerable<CanFrame> source, CancellationToken cancellationToken = default)
    {
        if (_cts is not null) throw new InvalidOperationException("Dispatcher has already started.");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pump = PumpAsync(source, _cts.Token);
        _batcher = BatchAsync(batchInterval ?? TimeSpan.FromMilliseconds(33), _cts.Token);
        return Task.CompletedTask;
    }
    private async Task PumpAsync(IAsyncEnumerable<CanFrame> source, CancellationToken ct)
    {
        try { await foreach (var frame in source.WithCancellation(ct).ConfigureAwait(false))
            if (_filters.Count == 0 || _filters.Any(filter => filter.Matches(frame))) { if (!_channel.Writer.TryWrite(frame)) Interlocked.Increment(ref _dropped); } }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { _channel.Writer.TryComplete(); }
    }
    private async Task BatchAsync(TimeSpan interval, CancellationToken ct)
    {
        var batch = new List<CanFrame>();
        using var timer = new PeriodicTimer(interval);
        try { while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) { while (_channel.Reader.TryRead(out var frame)) batch.Add(frame); if (batch.Count > 0) { FramesBatched?.Invoke(this, batch.ToArray()); batch.Clear(); } } }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        var cancellation = Interlocked.Exchange(ref _cts, null);
        if (cancellation is null)
        {
            return;
        }

        try
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            if (_pump is not null) await _pump.ConfigureAwait(false);
            if (_batcher is not null) await _batcher.ConfigureAwait(false);
        }
        finally
        {
            cancellation.Dispose();
        }
    }
}
