using Condux.Core.SourceMaps;
using Xunit;

namespace Condux.Core.Tests;

// The Turbopack sources are read from a real build of the dashboard. The webpack forms follow webpack's
// default template, webpack://[namespace]/[resourcePath], and its fallback, which appends ?[hash] when two
// modules would share a name. The namespace defaults to the package name. The plain path and the URL are
// shapes no bundler prefix applies to.
public class SourceMapPathsTests
{
    [Theory]
    [InlineData("turbopack:///[project]/web/src/components/form.tsx", "web/src/components/form.tsx")]
    [InlineData("webpack://_N_E/./src/lib/db.ts", "src/lib/db.ts")]
    [InlineData("webpack:///./src/checkout.ts", "src/checkout.ts")]
    [InlineData("webpack://@acme/web/./src/lib/db.ts", "src/lib/db.ts")]
    [InlineData("webpack://_N_E/./src/App.vue?4e2c", "src/App.vue")]
    [InlineData("webpack://_N_E/?1234", "webpack://_N_E/?1234")]
    [InlineData("src/already/clean.ts", "src/already/clean.ts")]
    [InlineData("https://cdn.example/app.js", "https://cdn.example/app.js")]
    public void A_bundler_source_becomes_the_repository_path_it_names(string source, string expected)
    {
        Assert.Equal(expected, SourceMapPaths.ToRepoPath(source));
    }

    [Theory]
    [InlineData("turbopack:///[project]/node_modules/.pnpm/clsx@2.1.1/node_modules/clsx/dist/clsx.mjs", true)]
    [InlineData("webpack://_N_E/./node_modules/clsx/dist/clsx.mjs", true)]
    [InlineData("turbopack:///[project]/web/node_modules/clsx/dist/clsx.mjs", true)]
    [InlineData("webpack:///webpack/runtime/define property getters", true)]
    [InlineData("turbopack:///[turbopack]/browser/runtime/base/runtime-base.ts", true)]
    [InlineData("webpack:///webpack/bootstrap", true)]
    [InlineData("webpack:///webpack/bootstrap 0f3c6ab1e2d4", true)]
    [InlineData("webpack:///./webpack/bootstrap.ts", false)]
    [InlineData("turbopack:///[project]/web/src/components/form.tsx", false)]
    [InlineData("webpack://_N_E/./src/lib/db.ts", false)]
    [InlineData("webpack:///./webpack/config.ts", false)] // an app folder that happens to be called webpack
    public void Installed_and_bundler_runtime_code_is_not_the_apps_own(string source, bool dependency)
    {
        Assert.Equal(dependency, SourceMapPaths.IsDependency(SourceMapPaths.ToRepoPath(source)));
    }
}
