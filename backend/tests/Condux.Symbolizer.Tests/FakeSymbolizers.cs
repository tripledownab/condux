namespace Condux.Symbolizer.Tests;

/// <summary>Shell scripts that stand in for llvm-symbolizer, for the behaviour the real binary cannot be made
/// to show on demand: hanging, crashing, answering short. Each lives in one directory removed on dispose.
/// They need a Unix shell.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal sealed class FakeSymbolizers : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "fake-symbolizer-" + Guid.NewGuid().ToString("N"));

    public FakeSymbolizers() => System.IO.Directory.CreateDirectory(Directory);

    public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);

    /// <summary>Writes an executable script with this body and returns its path.</summary>
    public string Write(string body)
    {
        var path = Path.Combine(Directory, "fake-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.UserWrite);
        return path;
    }
}
