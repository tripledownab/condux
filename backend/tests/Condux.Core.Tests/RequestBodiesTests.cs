using System.Security.Cryptography;
using System.Threading.Channels;
using Condux.Core.Http;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// Reading a request body a caller sized: whole and in order across the pooled pieces, refused past its
/// limit, and charged against a shared budget as it is allocated and given back on every way out. How slowly
/// a body may arrive is Kestrel's minimum data rate, which the control-plane sets (UploadBodiesTests).
/// </summary>
public sealed class RequestBodiesTests
{
    // Room for any body these tests send, with its pieces.
    private const int Plenty = 8 * RequestBodies.MaxPieceBytes;

    [Fact]
    public async Task A_body_spanning_many_pieces_is_read_whole_and_in_order()
    {
        // Several pooled pieces, and not a whole number of them.
        var bytes = RandomNumberGenerator.GetBytes(300_001);

        var bounded = await RequestBodies.ReadBoundedAsync(new MemoryStream(bytes), bytes.Length, default);
        var held = await RequestBodies.ReadHeldAsync(
            new MemoryStream(bytes), bytes.Length, new Budget(bytes.Length + Plenty), default);

        Assert.Equal(bytes, bounded);
        Assert.Equal(BodyRefusal.None, held.Refusal);
        Assert.Equal(bytes, held.Body!.Content);
    }

    [Fact]
    public async Task A_body_past_the_limit_is_refused_and_gives_back_what_it_took()
    {
        var budget = new Budget(Plenty);

        var bounded = await RequestBodies.ReadBoundedAsync(new MemoryStream(new byte[101]), 100, default);
        var held = await RequestBodies.ReadHeldAsync(
            new MemoryStream(new byte[101]), 100, budget, default);

        Assert.Null(bounded);
        Assert.Equal(new BodyRead(null, BodyRefusal.TooLarge), held);
        Assert.True(budget.IsWhole());
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    public async Task A_body_that_is_not_its_declared_encoding_is_refused_and_gives_back_what_it_took(string encoding)
    {
        // What the relay's decompression middleware hands the reader for a body that is not the encoding it
        // was sent as. The two decoders refuse differently: GZipStream throws InvalidDataException, BrotliStream
        // InvalidOperationException.
        var budget = new Budget(Plenty);
        Stream NotEncoded()
        {
            var bytes = new MemoryStream("not compressed at all"u8.ToArray());
            return encoding == "gzip"
                ? new System.IO.Compression.GZipStream(bytes, System.IO.Compression.CompressionMode.Decompress)
                : new System.IO.Compression.BrotliStream(bytes, System.IO.Compression.CompressionMode.Decompress);
        }

        var held = await RequestBodies.ReadHeldAsync(NotEncoded(), Plenty, budget, default);

        Assert.Equal(new BodyRead(null, BodyRefusal.Undecodable), held);
        Assert.True(budget.IsWhole());
        // The bounded read keeps throwing, so its null still means only too large.
        await Assert.ThrowsAsync<InvalidDataException>(() => RequestBodies.ReadBoundedAsync(NotEncoded(), Plenty, default));
    }

    [Fact]
    public async Task An_invalid_operation_the_decoder_did_not_raise_is_not_read_as_a_bad_body()
    {
        // Only the compression library's refusal is the sender's: the same exception type from anywhere
        // else is a fault of ours and must still escape.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RequestBodies.ReadHeldAsync(new ThrowingStream(), Plenty, new Budget(Plenty), default));
    }

    private sealed class ThrowingStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("not a decoder");
    }

    [Fact]
    public async Task A_body_past_the_limit_is_answered_as_too_large_even_when_the_budget_is_nearly_full()
    {
        // Room for the first piece and nothing more: the limit is checked before the bytes are charged, so the
        // sender is told its body is too large rather than to come back later and send it again.
        var budget = new Budget(RequestBodies.FirstPieceBytes);

        var held = await RequestBodies.ReadHeldAsync(new MemoryStream(new byte[101]), 100, budget, default);

        Assert.Equal(new BodyRead(null, BodyRefusal.TooLarge), held);
        Assert.True(budget.IsWhole());
    }

    [Fact]
    public async Task A_reader_is_charged_for_its_first_piece_before_any_byte_arrives()
    {
        // A connection that has sent nothing still holds a piece to read into, so it must be charged for it,
        // or connections that stall would hold memory the budget cannot see.
        var budget = new Budget(RequestBodies.FirstPieceBytes - 1);
        var stream = new TrickleStream();

        var refused = await Within(ReadAsync(stream, budget));

        Assert.Equal(new BodyRead(null, BodyRefusal.OverBudget), refused);
        Assert.False(stream.WasRead, "the reader read before it was charged for somewhere to read into");
        Assert.True(budget.IsWhole());
    }

    [Fact]
    public async Task Each_byte_is_charged_again_for_its_place_in_the_joined_body()
    {
        // 100 bytes in the first piece need the piece and 100 more for the body they are joined into.
        var tooSmall = new Budget(RequestBodies.FirstPieceBytes + 99);
        var enough = new Budget(RequestBodies.FirstPieceBytes + 100);

        var refused = await ReadAsync(new MemoryStream(new byte[100]), tooSmall);
        var read = await ReadAsync(new MemoryStream(new byte[100]), enough);

        Assert.Equal(new BodyRead(null, BodyRefusal.OverBudget), refused);
        Assert.True(tooSmall.IsWhole());
        Assert.Equal(100, read.Body!.Content.Length);
    }

    [Fact]
    public async Task A_body_refused_part_way_gives_back_what_it_had_taken()
    {
        // Room for the first piece and the bytes that fill it, and not for the second piece, twice the size,
        // that they then need.
        var budget = new Budget(3 * RequestBodies.FirstPieceBytes);
        var stream = new TrickleStream();
        var reading = ReadAsync(stream, budget);

        stream.Send(new byte[RequestBodies.FirstPieceBytes]);
        stream.Send(new byte[100]);
        stream.End();

        Assert.Equal(new BodyRead(null, BodyRefusal.OverBudget), await Within(reading));
        Assert.True(stream.WasRead);
        Assert.True(budget.IsWhole());
    }

    [Fact]
    public async Task A_held_body_keeps_its_own_size_until_it_is_disposed_once()
    {
        var budget = new Budget(Plenty);

        var read = await ReadAsync(new MemoryStream(new byte[100]), budget);
        var heldWhileUsed = !budget.TryReserve(Plenty - 99);
        var restWasFree = budget.TryReserve(Plenty - 100);
        budget.Release(Plenty - 100);
        read.Body!.Dispose();
        read.Body.Dispose();

        // The pieces went back when the body was joined, and only the body's own bytes stayed charged.
        Assert.True(heldWhileUsed);
        Assert.True(restWasFree);
        // Disposed twice and given back once: a second release would leave more free than the budget holds.
        Assert.True(budget.TryReserve(Plenty));
        Assert.False(budget.TryReserve(1));
    }

    [Fact]
    public async Task The_callers_own_cancellation_ends_the_read_and_gives_back_what_it_took()
    {
        var budget = new Budget(Plenty);
        var stream = new TrickleStream();
        using var cancel = new CancellationTokenSource();
        var reading = RequestBodies.ReadHeldAsync(stream, Plenty, budget, cancel.Token);

        await stream.WaitForReaderAsync();
        stream.Send(new byte[100]);
        await stream.WaitForReaderAsync();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(reading));
        Assert.True(budget.IsWhole());
    }

    // Bounded, so a reader that never finishes fails its test rather than hanging the run.
    private static Task<BodyRead> Within(Task<BodyRead> reading) => reading.WaitAsync(TimeSpan.FromSeconds(10));

    private static Task<BodyRead> ReadAsync(Stream stream, Budget budget) =>
        RequestBodies.ReadHeldAsync(stream, Plenty, budget, default);

    private sealed class Budget(long bytes) : ByteBudget(bytes)
    {
        private readonly long _bytes = bytes;

        /// <summary>Whether every byte is free again: the whole budget can be taken, and is then given back.</summary>
        public bool IsWhole()
        {
            var whole = TryReserve(_bytes);
            if (whole)
            {
                Release(_bytes);
            }
            return whole;
        }
    }

    /// <summary>A body that arrives a chunk at a time, when the test sends one.</summary>
    private sealed class TrickleStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private readonly SemaphoreSlim _asked = new(0);
        private byte[] _current = [];
        private int _offset;

        /// <summary>Whether the reader has asked for any bytes at all.</summary>
        public bool WasRead { get; private set; }

        public void Send(byte[] chunk) => _chunks.Writer.TryWrite(chunk);

        public void End() => _chunks.Writer.TryComplete();

        /// <summary>Completes once the reader has asked for more, so it has taken in all that was sent.</summary>
        public Task WaitForReaderAsync() => _asked.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(
            waited => Assert.True(waited.Result, "the reader never asked for more"), TaskScheduler.Default);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WasRead = true;
            if (_offset == _current.Length)
            {
                _asked.Release();
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken))
                {
                    return 0;
                }
                _current = await _chunks.Reader.ReadAsync(cancellationToken);
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
