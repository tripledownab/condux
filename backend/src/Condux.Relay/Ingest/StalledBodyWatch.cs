namespace Condux.Relay.Ingest;

/// <summary>
/// Closes the connection of an ingest body that has stopped arriving: one whose decompressed size has not
/// grown for <see cref="Limit"/> is aborted, which ends the read and gives its charge to the body budget
/// back, so a connection cannot keep that charge without sending the body.
/// </summary>
internal sealed class StalledBodyWatch : IAsyncDisposable
{
    /// <summary>How long a body may go without growing. Well past a pause on a slow uplink.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(1);

    private readonly ProgressStream _body;
    private readonly ITimer _timer;
    private int _done;

    public StalledBodyWatch(HttpContext ctx, TimeProvider clock)
    {
        _body = new ProgressStream(ctx.Request.Body, clock);
        // Kestrel's own abort, never a cancellation of the read: it ends the read at once and is logged
        // below Warning, where cancelling a read part way leaves Kestrel's reader mid-read.
        _timer = clock.CreateTimer(_ =>
        {
            if (Volatile.Read(ref _done) == 0 && clock.GetElapsedTime(_body.LastProgress) > Limit)
            {
                Abort(ctx);
            }
        }, null, CheckEvery, CheckEvery);
    }

    /// <summary>The body to read in place of the request's own.</summary>
    public Stream Body => _body;

    /// <summary>Stops watching, and waits for a check already running, so nothing is aborted once the read
    /// is over: by then the context may be answering this request or, recycled, the next one.</summary>
    public ValueTask DisposeAsync()
    {
        Volatile.Write(ref _done, 1);
        return _timer.DisposeAsync();
    }

    // A timer callback that throws ends the process, and the context can be torn down under a check that
    // started just before the read ended.
    private static void Abort(HttpContext ctx)
    {
        try
        {
            ctx.Abort();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class ProgressStream(Stream inner, TimeProvider clock) : Stream
    {
        private long _lastProgress = clock.GetTimestamp();

        public long LastProgress => Volatile.Read(ref _lastProgress);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await inner.ReadAsync(buffer, ct);
            if (read > 0)
            {
                Volatile.Write(ref _lastProgress, clock.GetTimestamp());
            }
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("an ingest body is read asynchronously");

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
