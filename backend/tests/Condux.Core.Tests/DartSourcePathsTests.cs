using Condux.Core.DebugFiles;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// The paths are shaped like those in real Flutter AOT debug files: every kind a real app's
/// arm64 symbols named (its own lib/, hosted and git pub packages, Flutter's packages, the Dart SDK,
/// dart:ui and .dart_tool), with the names changed. The rest are the layouts a CI or monorepo build adds.
/// </summary>
public class DartSourcePathsTests
{
    [Theory]
    // The app's own code, wherever the build ran.
    [InlineData("/home/runner/work/shop/shop/lib/screens/cart.dart", true)]
    [InlineData("/Users/dev/src/shop/lib/main.dart", true)]
    [InlineData(@"C:\a\shop\lib\main.dart", true)]
    // A local package in a monorepo is the app's code too.
    [InlineData("/repo/packages/core/lib/money.dart", true)]
    // A directory merely named hosted is not the pub cache.
    [InlineData("/srv/hosted/apps/shop-2/lib/main.dart", true)]
    // Hosted pub packages, in the default cache, a moved one, and Windows' cache.
    [InlineData("/Users/dev/.pub-cache/hosted/pub.dev/http-1.2.2/lib/src/client.dart", false)]
    [InlineData("/opt/cache/pub/hosted/pub.dev/collection-1.19.1/lib/src/list.dart", false)]
    [InlineData(@"C:\Users\dev\AppData\Local\Pub\Cache\hosted\pub.dev\http-1.2.2\lib\http.dart", false)]
    // A git dependency, from a SHA-1 and from a SHA-256 repository.
    [InlineData("/home/runner/.pub-cache/git/date_picker-0123456789abcdef0123456789abcdef01234567/lib/picker.dart", false)]
    [InlineData("/home/runner/.pub-cache/git/date_picker-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/lib/picker.dart", false)]
    // Flutter packages pub finds in the SDK's cache rather than under packages/.
    [InlineData("/opt/flutter/bin/cache/pkg/flutter_gpu/lib/src/gpu.dart", false)]
    // A plain Dart AOT build may leave a file URI, which is read as the path it names.
    [InlineData("file:///home/runner/work/shop/shop/lib/main.dart", true)]
    [InlineData("file:///home/runner/.pub-cache/hosted/pub.dev/http-1.2.2/lib/http.dart", false)]
    // Flutter's own packages.
    [InlineData("/opt/flutter/packages/flutter/lib/src/widgets/framework.dart", false)]
    [InlineData("/opt/flutter/packages/flutter_localizations/lib/src/material_localizations.dart", false)]
    // The Dart SDK and dart:ui, which the file names relative.
    [InlineData("third_party/dart/sdk/lib/core/errors.dart", false)]
    [InlineData("lib/ui/window.dart", false)]
    // Generated code.
    [InlineData("/home/runner/work/shop/shop/.dart_tool/flutter_build/dart_plugin_registrant.dart", false)]
    public void A_dart_path_is_the_apps_own_code_only_outside_sdk_packages_and_generated_code(string path, bool inApp)
    {
        Assert.Equal(inApp, DartSourcePaths.ToInApp(path));
    }

    [Theory]
    [InlineData("/home/dev/native/crash.cpp")]
    [InlineData("/Users/dev/src/shop/lib")]
    // A package URI names only a package, which says nothing about whose code it is.
    [InlineData("package:shop/main.dart")]
    [InlineData(null)]
    public void A_path_that_is_not_dart_or_names_only_a_package_says_nothing(string? path)
    {
        Assert.Null(DartSourcePaths.ToInApp(path));
    }
}
