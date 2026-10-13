namespace Condux.Tests.Shared;

/// <summary>Finds an llvm-symbolizer for the tests that need the real one: the configured path first, then
/// the names and locations LLVM 18 installs under on Ubuntu. Fails loudly when there is none.</summary>
internal static class SymbolizerBinary
{
    private static readonly string[] Candidates =
    [
        "/usr/lib/llvm-18/bin/llvm-symbolizer",
        "/usr/bin/llvm-symbolizer-18",
        "/usr/bin/llvm-symbolizer",
    ];

    public static string Locate()
    {
        if (Environment.GetEnvironmentVariable("CONDUX_LLVM_SYMBOLIZER") is { Length: > 0 } configured)
        {
            return File.Exists(configured)
                ? configured
                : throw new InvalidOperationException($"CONDUX_LLVM_SYMBOLIZER names {configured}, which does not exist");
        }

        return Candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException(
                "no llvm-symbolizer found; install LLVM 18 or set CONDUX_LLVM_SYMBOLIZER to its path");
    }
}
