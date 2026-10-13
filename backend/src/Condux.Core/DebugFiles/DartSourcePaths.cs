using System.Text.RegularExpressions;

namespace Condux.Core.DebugFiles;

/// <summary>
/// Whether a source path a Flutter app's debug file names is the app's own code. An obfuscated crash from
/// sentry_flutter (seen on 9.30.1) carries no <c>in_app</c> at all, so once a frame resolves to a path,
/// that path is the only thing that can say it; without it no resolved frame counts as the app's, and
/// nothing scoped to in-app frames (the fix context, suggested code mappings, the issue page) finds any.
///
/// Read against the paths in real AOT debug files. The app's sources are absolute build-machine paths.
/// The Dart SDK and <c>dart:ui</c> are relative (<c>third_party/dart/sdk/...</c>, <c>lib/ui/...</c>), so
/// a relative path is not taken as the app's. Flutter's split debug info resolves every path so
/// (<c>--resolve-dwarf-paths</c>);
/// a plain Dart AOT build may leave <c>file://</c> and <c>package:</c> URIs, the first read as a path and
/// the second left to what the SDK said. Packages are recognised by the pub cache's own layout, not
/// by where the cache sits, since <c>PUB_CACHE</c> can move it anywhere. Flutter's packages are recognised
/// by the names the SDK ships them under, and <c>.dart_tool</c> holds generated code no repository carries.
/// </summary>
public static partial class DartSourcePaths
{
    /// <summary>True for the app's own code, false for SDK, package or generated code, and null for a path
    /// that is not Dart, whose frame keeps what its SDK said.</summary>
    public static bool? ToInApp(string? path)
    {
        if (path is null || !path.EndsWith(".dart", StringComparison.Ordinal))
        {
            return null;
        }
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("file://", StringComparison.Ordinal))
        {
            normalized = Uri.UnescapeDataString(normalized["file://".Length..]);
        }
        else if (normalized.Contains("://", StringComparison.Ordinal) || normalized.StartsWith("package:", StringComparison.Ordinal))
        {
            // A URI the build left unresolved says where the code came from only by a package name.
            return null;
        }
        return IsAbsolute(normalized)
            && !NotTheApp().IsMatch(normalized);
    }

    private static bool IsAbsolute(string path) =>
        path.StartsWith('/') || (path.Length > 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/');

    // A hosted package (hosted/<host>/<name>-<major>.<minor>.<patch>.../), a git one (git/<name>-<commit>/,
    // SHA-1 or SHA-256), a package of the Flutter SDK, under packages/ or bin/cache/pkg/ where pub also
    // finds them, or generated code.
    [GeneratedRegex(
        @"/hosted/[^/]+/[^/]+-\d+\.\d+\.\d+[^/]*/|/git/[^/]+-[0-9a-f]{40}(?:[0-9a-f]{24})?/"
        + @"|/packages/(flutter|flutter_localizations|flutter_web_plugins|flutter_test|flutter_driver"
        + @"|integration_test)/lib/|/bin/cache/pkg/"
        + @"|/\.dart_tool/")]
    private static partial Regex NotTheApp();
}
