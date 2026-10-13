namespace Condux.Core.DebugFiles;

// The symbolizer service's wire shapes, here so the service and every caller read one definition.
// Addresses travel as "0x" hex strings, since a 64-bit address does not survive a JSON number in every
// client.

/// <summary>What one call to the symbolizer may carry, read by the service and by its callers alike.</summary>
public static class SymbolizerLimits
{
    /// <summary>The addresses one resolve takes. A caller with more sends the ones that matter most.</summary>
    public const int MaxAddressesPerBatch = 512;
}

/// <summary>Offsets inside one debug file's image to resolve, in the caller's order. The file is named by
/// the SHA-256 of its bytes.</summary>
public sealed record ResolveRequest(string File, IReadOnlyList<string> Addresses);

/// <summary>One result per requested address, in the request's order.</summary>
public sealed record ResolveResponse(IReadOnlyList<ResolvedAddress> Results);

/// <summary>The frames an address resolved to, innermost first as llvm-symbolizer reports them: an inlined
/// call yields the inlined function, then each caller it was inlined into. Empty when nothing is known.</summary>
public sealed record ResolvedAddress(string Address, IReadOnlyList<ResolvedFrame> Frames);

/// <summary>A source location. Absent values are null or 0, never llvm-symbolizer's "??".</summary>
public sealed record ResolvedFrame(string? Function, string? File, int Line, int Column);
