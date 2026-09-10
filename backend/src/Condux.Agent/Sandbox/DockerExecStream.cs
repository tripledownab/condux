using System.Buffers.Binary;
using System.Text;

namespace Condux.Agent.Sandbox;

/// <summary>
/// Docker's framing for the output of an exec, and the cap on how much of it is kept.
///
/// Its own file because it is the one part of talking to the daemon that is not talking to the daemon:
/// pure stream decoding, testable against a MemoryStream with no container anywhere. That was already
/// true before it was split out, which is why its tests had been living in the tar writer's test file
/// for want of a home of their own.
/// </summary>
internal static class DockerExecStream
{
    /// <summary>
    /// How much of a command's output is read before it is cut off. A runaway command that prints forever
    /// must not be able to exhaust the worker's memory, and the model only ever sees a bounded excerpt
    /// anyway, so reading more would be spending memory on text nobody reads.
    /// </summary>
    internal const int MaxCapturedBytes = 1024 * 1024;

    /// <summary>
    /// Docker frames an untty'd exec stream as an 8 byte header (stream id, then a big-endian length)
    /// followed by that many payload bytes. Stdout and stderr are merged here in stream order, which is how
    /// a person reads a build log and therefore how the model should see it.
    /// </summary>
    internal static async Task<string> ReadMultiplexedAsync(Stream stream, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var header = new byte[8];

        while (output.Length < MaxCapturedBytes)
        {
            if (!await ReadExactlyAsync(stream, header, cancellationToken))
            {
                break;
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
            if (length <= 0)
            {
                continue;
            }

            var payload = new byte[Math.Min(length, MaxCapturedBytes)];
            if (!await ReadExactlyAsync(stream, payload, cancellationToken))
            {
                break;
            }

            output.Append(Encoding.UTF8.GetString(payload));
        }

        return output.ToString();
    }

    private static async Task<bool> ReadExactlyAsync(
        Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (got == 0)
            {
                return false;
            }

            read += got;
        }

        return true;
    }
}
