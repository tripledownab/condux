using System.Buffers.Binary;
using System.Text;
using Condux.Agent.Sandbox;
using Xunit;

namespace Condux.Conductor.Tests;

/// <summary>
/// Docker's exec framing, read back against a MemoryStream. These tests used to live in the tar
/// writer's file because the code under test had no file of its own; both now do.
/// </summary>
public class DockerExecStreamTests
{
    [Fact]
    public async Task A_framed_exec_stream_is_read_back_in_order()
    {
        // Docker frames an untty'd exec stream as an 8 byte header then that many payload bytes, merging
        // stdout and stderr in stream order. Reading it as plain text would splice the headers into the
        // output the model sees.
        var stream = new MemoryStream(Frame((1, "compiling\n"), (2, "warning: unused\n"), (1, "done\n")));

        var output = await DockerExecStream.ReadMultiplexedAsync(stream, CancellationToken.None);

        Assert.Equal("compiling\nwarning: unused\ndone\n", output);
    }

    [Fact]
    public async Task A_truncated_frame_ends_the_read_rather_than_hanging()
    {
        var complete = Frame((1, "partial output\n"));
        // A daemon that dies mid-frame leaves a header promising bytes that never arrive.
        var truncated = complete.Concat(new byte[] { 1, 0, 0, 0, 0, 0, 0, 40 }).ToArray();

        var output = await DockerExecStream.ReadMultiplexedAsync(
            new MemoryStream(truncated), CancellationToken.None);

        Assert.Equal("partial output\n", output);
    }

    private static byte[] Frame(params (byte Stream, string Text)[] chunks)
    {
        var buffer = new List<byte>();
        foreach (var (streamId, text) in chunks)
        {
            var payload = Encoding.UTF8.GetBytes(text);
            var header = new byte[8];
            header[0] = streamId;
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), payload.Length);
            buffer.AddRange(header);
            buffer.AddRange(payload);
        }

        return [.. buffer];
    }
}
