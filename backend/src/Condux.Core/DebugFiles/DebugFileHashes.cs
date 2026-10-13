using System.Security.Cryptography;

namespace Condux.Core.DebugFiles;

/// <summary>
/// The name a debug file goes by in the symbolizer: the SHA-256 of its bytes, in lowercase hex. Not its build
/// ID, which is a note the file's author writes and so could name another project's build. The upload
/// records it and the symbolizer checks it, so both compute it here.
/// </summary>
public static class DebugFileHashes
{
    public static string ToSha256(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));
}
