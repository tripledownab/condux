using Condux.Core.Repos;
using Condux.Core.SourceControl;
using Xunit;

namespace Condux.Core.Tests;

public class CodeMappingDeriverTests
{
    [Fact]
    public void A_resolved_flutter_frame_names_its_build_path_and_derives_the_rule_back_to_the_repo()
    {
        // Native symbolication names a Dart frame by the absolute path the app was built from.
        const string Built = "/home/runner/work/shop/shop/lib/screens/cart.dart";
        var derived = CodeMappingDeriver.Derive(
            [Built, "/home/runner/work/shop/shop/lib/main.dart"],
            ["lib/screens/cart.dart", "lib/main.dart", "pubspec.yaml"]);

        var rule = derived[0];
        var mapping = new CodeMapping(Guid.NewGuid(), Guid.NewGuid(), rule.StackRoot, rule.SourceRoot);
        Assert.Equal("lib/screens/cart.dart", CodeMapper.Resolve(Built, [mapping]));
        Assert.Equal(2, rule.MatchCount);
    }

    // esbuild, Vite, terser, Babel and swc name a map's sources relative to the map file, so a symbolicated
    // frame from them climbs out of the build directory (Vite 8.3.4: dist/assets/*.map names
    // ../../src/App.tsx). Such a frame names no repository file until a mapping rewrites the climb, which
    // Suggest from errors must therefore offer, for an app at the repository root and for one inside a
    // monorepo with no sibling app holding the same files (siblings that do are ambiguous). Through
    // Suggest, the call the endpoint makes, not Derive alone.
    [Theory]
    [InlineData("", "src/App.tsx")]
    [InlineData("apps/web/", "apps/web/src/App.tsx")]
    public void A_map_relative_frame_from_a_vite_build_derives_the_rule_back_into_the_repo(
        string appRoot, string expected)
    {
        string[] frames = ["../../src/App.tsx", "../../src/main.tsx"];
        var suggested = CodeMappingDeriver.Suggest(
            frames, [$"{appRoot}src/App.tsx", $"{appRoot}src/main.tsx", $"{appRoot}package.json"], [], []);

        Assert.Equal(MappingSuggestions.Suggested, suggested.Outcome);
        var rule = Assert.Single(suggested.Suggestions);
        var mapping = new CodeMapping(Guid.NewGuid(), Guid.NewGuid(), rule.StackRoot, rule.SourceRoot);
        Assert.Equal(expected, CodeMapper.Resolve(frames[0], [mapping]));
        Assert.True(RepoPaths.IsRepoRelative(CodeMapper.Resolve(frames[0], [mapping])));
        Assert.Equal(2, rule.MatchCount);
    }

    [Fact]
    public void Derives_ThePrefixRewrite_FromASharedFileSuffix()
    {
        var derived = CodeMappingDeriver.Derive(
            ["/app/dist/checkout.js"], ["src/checkout.js", "src/cart.js"]);

        var rule = Assert.Single(derived);
        Assert.Equal("/app/dist/", rule.StackRoot);
        Assert.Equal("src/", rule.SourceRoot);
        Assert.Equal(1, rule.MatchCount);
    }

    [Fact]
    public void AggregatesAcrossFrames_AndCountsSupport()
    {
        var derived = CodeMappingDeriver.Derive(
            ["/app/dist/a.js", "/app/dist/b.js", "/app/dist/lib/c.js"],
            ["src/a.js", "src/b.js", "src/lib/c.js"]);

        var rule = Assert.Single(derived);
        Assert.Equal("/app/dist/", rule.StackRoot);
        Assert.Equal("src/", rule.SourceRoot);
        Assert.Equal(3, rule.MatchCount); // all three frames agree on the same prefix rewrite
    }

    [Fact]
    public void PrefersTheLongestMatchingSuffix_WhenARepoHasNestedCopies()
    {
        // "util/log.js" matches by 2 segments; "packages/app/util/log.js" matches by 3, so the deeper,
        // more specific rewrite wins.
        var derived = CodeMappingDeriver.Derive(
            ["/build/app/util/log.js"], ["util/log.js", "packages/app/util/log.js"]);

        var rule = Assert.Single(derived);
        Assert.Equal("/build/", rule.StackRoot);
        Assert.Equal("packages/", rule.SourceRoot);
    }

    // Two sibling apps from one template both hold src/App.tsx, and a frame of either matches both to the
    // same depth. Nothing in the frame says which app it came from, so it supports no rule, where it used to
    // pick the shorter source root and send the other app's frames to the wrong one.
    [Fact]
    public void A_frame_matching_two_repo_files_equally_well_supports_no_rule_and_says_so()
    {
        var suggestions = CodeMappingDeriver.Suggest(
            ["../../src/App.tsx", "../../src/main.tsx"],
            ["apps/admin/src/App.tsx", "apps/admin/src/main.tsx", "apps/web/src/App.tsx", "apps/web/src/main.tsx"],
            [], []);

        Assert.Empty(suggestions.Suggestions);
        Assert.Equal(MappingSuggestions.Ambiguous, suggestions.Outcome);
    }

    // A frame that matches one app alone still decides the rule, and the frames that tie neither support it
    // nor pull a second rule toward the other app. The tie-break this replaced chose apps/web, the shorter.
    [Fact]
    public void A_frame_only_one_app_holds_still_decides_the_rule()
    {
        var suggestions = CodeMappingDeriver.Suggest(
            ["../../src/App.tsx", "../../src/pages/Billing.tsx"],
            ["apps/admin/src/App.tsx", "apps/admin/src/pages/Billing.tsx", "apps/web/src/App.tsx"],
            [], []);

        var rule = Assert.Single(suggestions.Suggestions);
        Assert.Equal(("../../", "apps/admin/"), (rule.StackRoot, rule.SourceRoot));
        Assert.Equal(1, rule.MatchCount);
        Assert.Equal(MappingSuggestions.Suggested, suggestions.Outcome);
    }

    // A frame that tied still holds the rules offered beside it to its own files. An Android build keeps
    // Tools.kt in both flavors, so the main source set's rule would send it to a main file that does not
    // exist; the rule is carried a file at a time instead, and Tools.kt is left unmapped.
    [Fact]
    public void A_tied_frame_is_not_sent_to_a_file_outside_the_ones_it_tied_between()
    {
        string[] frames = ["com/acme/A.kt", "com/acme/B.kt", "com/acme/Tools.kt"];
        var suggestions = CodeMappingDeriver.Suggest(
            frames,
            [
                "app/src/main/java/com/acme/A.kt", "app/src/main/java/com/acme/B.kt",
                "app/src/debug/java/com/acme/Tools.kt", "app/src/release/java/com/acme/Tools.kt",
            ],
            [], []);

        IReadOnlyList<CodeMapping> rules =
            [.. suggestions.Suggestions.Select(rule => Mapped(rule.StackRoot, rule.SourceRoot))];
        Assert.Equal("app/src/main/java/com/acme/A.kt", CodeMapper.Resolve(frames[0], rules));
        Assert.Equal("app/src/main/java/com/acme/B.kt", CodeMapper.Resolve(frames[1], rules));
        Assert.Null(CodeMapper.Resolve(frames[2], rules));
    }

    // Held to its files while rules are chosen, not only by the final pass, a tied frame costs only its own
    // package's rule: the other package keeps a rule of its own rather than coming back a file at a time.
    [Fact]
    public void A_tied_frame_costs_only_its_own_package_its_rule()
    {
        var derived = CodeMappingDeriver.Derive(
            ["com/acme/x/A.kt", "com/acme/x/A2.kt", "com/acme/y/B.kt", "com/acme/y/Tools.kt"],
            [
                "app/src/main/java/com/acme/x/A.kt", "app/src/main/java/com/acme/x/A2.kt",
                "app/src/main/java/com/acme/y/B.kt",
                "app/src/debug/java/com/acme/y/Tools.kt", "app/src/release/java/com/acme/y/Tools.kt",
            ]);

        Assert.Equal(
            [("com/acme/x/", "app/src/main/java/com/acme/x/"), ("com/acme/y/B.kt", "app/src/main/java/com/acme/y/B.kt")],
            derived.Select(rule => (rule.StackRoot, rule.SourceRoot)).Order());
    }

    // A frame that shares only its file name with two repo files is what a frame from another repository or
    // branch meets too, so with nothing else matched the answer is still no match.
    [Fact]
    public void A_tie_on_the_file_name_alone_answers_no_match()
    {
        var suggestions = CodeMappingDeriver.Suggest(
            ["/app/dist/index.js", "/app/dist/handler.js"], ["index.js", "lib/index.js", "README.md"], [], []);

        Assert.Empty(suggestions.Suggestions);
        Assert.Equal(MappingSuggestions.NoMatch, suggestions.Outcome);
    }

    // A frame that ties on its file name alone (a renamed index.ts, gone from the branch) counts as matching
    // nothing, so it does not break up the rule the other frames support, and the project's other files keep
    // their mapping.
    [Fact]
    public void A_stale_frame_tied_on_its_file_name_keeps_the_main_rule()
    {
        var suggestions = CodeMappingDeriver.Suggest(
            ["/build/src/a.ts", "/build/src/b/c.ts", "/build/src/gone/index.ts"],
            ["web/src/a.ts", "web/src/b/c.ts", "web/src/x/index.ts", "web/src/y/index.ts", "web/src/z/unsampled.ts"],
            [], []);

        var rule = Assert.Single(suggestions.Suggestions);
        Assert.Equal(("/build/", "web/"), (rule.StackRoot, rule.SourceRoot));
    }

    // Frames that match nothing beside one that ties past its file name are what another repository looks
    // like (a monorepo's packages each tend to hold a src/index.ts), so the answer stays no match.
    [Fact]
    public void A_deep_tie_beside_frames_that_match_nothing_answers_no_match()
    {
        var suggestions = CodeMappingDeriver.Suggest(
            ["/app/src/index.ts", "/app/src/handler.ts", "/app/src/db.ts"],
            ["packages/a/src/index.ts", "packages/b/src/index.ts", "README.md"],
            [], []);

        Assert.Equal(MappingSuggestions.NoMatch, suggestions.Outcome);
    }

    // A frame that ties on its file name alone counts as matching nothing for the answer too.
    [Fact]
    public void A_deep_tie_beside_a_tie_on_the_file_name_answers_no_match()
    {
        var suggestions = CodeMappingDeriver.Suggest(
            ["../../src/App.tsx", "../../lib/index.ts"],
            ["apps/a/src/App.tsx", "apps/b/src/App.tsx", "p/index.ts", "q/index.ts"],
            [], []);

        Assert.Equal(MappingSuggestions.NoMatch, suggestions.Outcome);
    }

    [Fact]
    public void A_repo_file_listed_twice_is_not_a_tie_with_itself()
    {
        var derived = CodeMappingDeriver.Derive(["/build/src/App.tsx"], ["web/src/App.tsx", @"web\src\App.tsx"]);

        var rule = Assert.Single(derived);
        Assert.Equal(("/build/", "web/"), (rule.StackRoot, rule.SourceRoot));
    }

    [Fact]
    public void A_deeper_match_settles_a_tie_found_before_it()
    {
        var derived = CodeMappingDeriver.Derive(
            ["/build/web/src/App.tsx"], ["admin/src/App.tsx", "other/src/App.tsx", "apps/web/src/App.tsx"]);

        var rule = Assert.Single(derived);
        Assert.Equal(("/build/", "apps/"), (rule.StackRoot, rule.SourceRoot));
    }

    [Fact]
    public void NormalizesWindowsSeparators()
    {
        var derived = CodeMappingDeriver.Derive([@"C:\app\dist\x.js"], ["src/x.js"]);

        var rule = Assert.Single(derived);
        Assert.Equal("C:/app/dist/", rule.StackRoot);
        Assert.Equal("src/", rule.SourceRoot);
    }

    [Fact]
    public void DerivesTheBuildRootRewrite_ForACompiledAppWhoseFramesCarryItsBuildPath()
    {
        // The most common mapping a real project needs, and the one nothing covered: a container image
        // builds under some root, the compiled artifacts record that root, and every runtime frame carries
        // it. The rewrite that fixes it is a bare prefix strip, so the source root is empty.
        var derived = CodeMappingDeriver.Derive(
            [
                "/src/backend/src/Condux.ControlPlane/Program.cs",
                "/src/backend/src/Condux.Storage/Postgres/IssueRepository.cs",
            ],
            [
                "backend/src/Condux.ControlPlane/Program.cs",
                "backend/src/Condux.Storage/Postgres/IssueRepository.cs",
                "web/src/app/page.tsx",
            ]);

        var rule = Assert.Single(derived);
        Assert.Equal("/src/", rule.StackRoot);
        Assert.Equal("", rule.SourceRoot);
        Assert.Equal(2, rule.MatchCount);
    }

    [Fact]
    public void TheDerivedBuildRootRewrite_IsOneCodeMapperCanActuallyApply()
    {
        // The deriver and the resolver have to agree on the leading separator or the suggestion is
        // plausible and inert: it would list in the UI, add cleanly, and silently match no frame.
        const string frame = "/src/backend/src/Condux.ControlPlane/Program.cs";
        var rule = Assert.Single(CodeMappingDeriver.Derive(
            [frame], ["backend/src/Condux.ControlPlane/Program.cs"]));

        var resolved = CodeMapper.Resolve(
            frame, [new CodeMapping(Guid.Empty, Guid.Empty, rule.StackRoot, rule.SourceRoot)]);

        Assert.Equal("backend/src/Condux.ControlPlane/Program.cs", resolved);
    }

    // A Turbopack build symbolicates to repo-relative paths, which change under no rule, but a frame is
    // linked to GitHub only through a mapping, so the identity rule is what such a project needs.
    [Fact]
    public void SuggestsTheIdentityRule_ForFramesThatAreAlreadyRepoRelative()
    {
        var rule = Assert.Single(CodeMappingDeriver.Derive(["src/app.js", "src/other.js"], ["src/app.js", "src/other.js"]));

        Assert.Equal(("", "", 2), (rule.StackRoot, rule.SourceRoot, rule.MatchCount));
        Assert.Equal("src/app.js", CodeMapper.Resolve("src/app.js", [new CodeMapping(Guid.NewGuid(), Guid.NewGuid(), "", "")]));
    }

    [Fact]
    public void SuggestsOnlyTheIdentityForAFrameThatNamesARepoFile_EvenWhenOtherFilesShareItsName()
    {
        // A symbolicated Next.js frame is already repo-relative, and a Next app has many files named
        // page.tsx. Pairing the frame with a different one would map it to the wrong file.
        var rule = Assert.Single(CodeMappingDeriver.Derive(
            ["web/app/settings/general/page.tsx"],
            ["web/app/settings/general/page.tsx", "web/app/page.tsx", "web/app/login/page.tsx"]));

        Assert.Equal(("", ""), (rule.StackRoot, rule.SourceRoot));
    }

    // The identity rule's stack root is empty, the shortest there is, so a narrower rule beside it wins.
    [Fact]
    public void TheIdentityRuleNeverOverridesANarrowerOne()
    {
        var identity = new CodeMapping(Guid.NewGuid(), Guid.NewGuid(), "", "");
        var narrow = new CodeMapping(Guid.NewGuid(), Guid.NewGuid(), "/app/dist/", "src/");

        Assert.Equal("src/x.js", CodeMapper.Resolve("/app/dist/x.js", [identity, narrow]));
        Assert.Equal("src/x.js", CodeMapper.Resolve("/app/dist/x.js", [narrow, identity]));
        Assert.Equal("web/app/page.tsx", CodeMapper.Resolve("web/app/page.tsx", [identity, narrow]));
    }

    [Fact]
    public void ReturnsNothing_WhenNoFrameMatchesTheTree()
    {
        Assert.Empty(CodeMappingDeriver.Derive(["/app/vendor/x.js"], ["src/y.js", "src/z.js"]));
    }

    [Fact]
    public void RanksMoreStronglySupportedRulesFirst()
    {
        // Two frames support the /dist/ → src/ rewrite, one supports /other/ → lib/; the stronger leads.
        var derived = CodeMappingDeriver.Derive(
            ["/dist/a.js", "/dist/b.js", "/other/c.js"],
            ["src/a.js", "src/b.js", "lib/c.js"]);

        Assert.Equal(2, derived.Count);
        Assert.Equal(("/dist/", "src/", 2), (derived[0].StackRoot, derived[0].SourceRoot, derived[0].MatchCount));
        Assert.Equal(("/other/", "lib/", 1), (derived[1].StackRoot, derived[1].SourceRoot, derived[1].MatchCount));
    }

    // Each empty answer names its reason, so the dashboard can say "send errors", "check the repo" or
    // "nothing to add" instead of one message for all three.
    [Fact]
    public void Suggest_NamesWhyItHasNothingToOffer()
    {
        string[] tree = ["src/app.js"];
        CodeMapping[] none = [];

        Assert.Equal(MappingSuggestions.NoFrames, CodeMappingDeriver.Suggest([], tree, none, []).Outcome);
        Assert.Equal(MappingSuggestions.NoMatch, CodeMappingDeriver.Suggest(["/vendor/x.js"], tree, none, []).Outcome);
        Assert.Equal(
            MappingSuggestions.AlreadyMapped,
            CodeMappingDeriver.Suggest(["/app/dist/app.js"], tree, [Mapped("/app/dist/", "src/")], []).Outcome);

        var offered = CodeMappingDeriver.Suggest(["/app/dist/app.js", "src/app.js"], tree, [Mapped("", "")], []);
        Assert.Equal(MappingSuggestions.Suggested, offered.Outcome);
        Assert.Equal("/app/dist/", Assert.Single(offered.Suggestions).StackRoot);

        // Frames that fit nothing do not hide the ones that do.
        var mixed = CodeMappingDeriver.Suggest(["src/app.js", "/vendor/x.js"], tree, none, []);
        Assert.Equal(MappingSuggestions.Suggested, mixed.Outcome);
        Assert.Equal(("", ""), (Assert.Single(mixed.Suggestions).StackRoot, mixed.Suggestions[0].SourceRoot));
    }

    // A Next app that moved from webpack to Turbopack keeps its webpack rule "" to "web/", while its new
    // frames already name repo files and want "" to "". The stack root is taken by a rule pointing
    // elsewhere, so the links are wrong and "nothing to add" would hide it.
    [Fact]
    public void Suggest_CallsAStackRootMappedElsewhereAConflictNotAMatch()
    {
        var answer = CodeMappingDeriver.Suggest(
            ["web/app/page.tsx"], ["web/app/page.tsx"], [Mapped("", "web/")], []);

        Assert.Equal(MappingSuggestions.Conflicting, answer.Outcome);
        Assert.Empty(answer.Suggestions);
    }

    // A JVM frame is its package path, so a rule derived from it rewrites from the empty root. Saved as
    // "" → src/main/java/ it would rewrite every frame the project sends, a JS one included.
    [Fact]
    public void Narrows_an_empty_root_rewrite_to_the_package_its_frames_share()
    {
        var rule = Assert.Single(CodeMappingDeriver.Derive(
            ["com/acme/billing/Invoice.java", "com/acme/web/Home.java"],
            ["src/main/java/com/acme/billing/Invoice.java", "src/main/java/com/acme/web/Home.java"]));

        Assert.Equal(("com/acme/", "src/main/java/com/acme/", 2), (rule.StackRoot, rule.SourceRoot, rule.MatchCount));
        var mapping = Mapped(rule.StackRoot, rule.SourceRoot);
        Assert.Equal("src/main/java/com/acme/web/Home.java", CodeMapper.Resolve("com/acme/web/Home.java", [mapping]));
        Assert.Null(CodeMapper.Resolve("static/js/app.js", [mapping]));
    }

    // Each module of a multi-module build used to get "" → <module>/src/main/java/, and those rules tie.
    [Fact]
    public void Gives_each_module_of_a_multi_module_build_its_own_rule()
    {
        var rules = CodeMappingDeriver.Derive(
            ["com/acme/billing/Invoice.java", "com/acme/billing/tax/Rate.java", "com/acme/web/Home.java"],
            [
                "billing/src/main/java/com/acme/billing/Invoice.java",
                "billing/src/main/java/com/acme/billing/tax/Rate.java",
                "web/src/main/java/com/acme/web/Home.java",
            ]);

        Assert.Equal(
            [
                ("com/acme/billing/", "billing/src/main/java/com/acme/billing/"),
                ("com/acme/web/", "web/src/main/java/com/acme/web/"),
            ],
            rules.Select(rule => (rule.StackRoot, rule.SourceRoot)));
    }

    // Two rules on one stack root tie in CodeMapper, which would pick between them by list order. The best
    // supported keeps the root, and a frame it would misroute gets a rule of its own file.
    [Fact]
    public void A_tie_on_one_stack_root_keeps_the_best_supported_and_carries_the_rest_per_file()
    {
        string[] frames = ["x/Invoice.java", "x/Ledger.java", "x/Home.java"];
        string[] tree = ["billing/x/Invoice.java", "billing/x/Ledger.java", "web/x/Home.java"];

        Assert.Equal(
            [("x/", "billing/x/", 2), ("x/Home.java", "web/x/Home.java", 1)],
            CodeMappingDeriver.Derive(frames, tree).Select(rule => (rule.StackRoot, rule.SourceRoot, rule.MatchCount)));
        AssertNoFrameIsMisrouted(frames, tree);
    }

    [Fact]
    public void Two_modules_that_narrow_to_one_root_each_keep_their_frames()
    {
        string[] frames = ["com/acme/billing/A.java", "com/acme/core/C.java", "com/acme/web/B.java", "com/acme/api/D.java"];
        string[] tree =
        [
            "m1/src/main/java/com/acme/billing/A.java",
            "m1/src/main/java/com/acme/core/C.java",
            "m2/src/main/java/com/acme/web/B.java",
            "m2/src/main/java/com/acme/api/D.java",
        ];

        Assert.Equal(
            [
                ("com/acme/", "m1/src/main/java/com/acme/"),
                ("com/acme/api/", "m2/src/main/java/com/acme/api/"),
                ("com/acme/web/", "m2/src/main/java/com/acme/web/"),
            ],
            CodeMappingDeriver.Derive(frames, tree).Select(rule => (rule.StackRoot, rule.SourceRoot)).Order());
        AssertNoFrameIsMisrouted(frames, tree);
    }

    // Frames under com/ and org/ share no directory, and narrowing them together would leave the empty
    // root rewriting every frame.
    [Fact]
    public void Frames_under_different_leading_directories_get_a_rule_each()
    {
        var rules = CodeMappingDeriver.Derive(
            ["com/acme/A.java", "org/example/B.java"],
            ["src/main/java/com/acme/A.java", "src/main/java/org/example/B.java"]);

        Assert.Equal(
            [("com/acme/", "src/main/java/com/acme/"), ("org/example/", "src/main/java/org/example/")],
            rules.Select(rule => (rule.StackRoot, rule.SourceRoot)).Order());
    }

    // Equal support on one stack root: the shorter source root takes it, then the ordinally smaller, so the
    // answer does not depend on the order frames arrive in.
    [Fact]
    public void Breaks_an_equal_tie_on_one_stack_root_by_the_shorter_then_smaller_source_root()
    {
        Assert.Equal(
            ("x/", "web/x/"),
            ToRoots(CodeMappingDeriver.Derive(["x/Invoice.java", "x/Home.java"], ["billing/x/Invoice.java", "web/x/Home.java"])[0]));
        Assert.Equal(
            ("x/", "aa/x/"),
            ToRoots(CodeMappingDeriver.Derive(["x/Invoice.java", "x/Home.java"], ["bb/x/Invoice.java", "aa/x/Home.java"])[0]));
    }

    private static (string, string) ToRoots(DerivedMapping rule) => (rule.StackRoot, rule.SourceRoot);

    [Fact]
    public void Routes_every_sampled_frame_to_the_file_it_matched_when_one_root_nests_in_another()
    {
        string[] frames = ["com/acme/X.java", "com/acme/b/e/R.java", "com/acme/b/c/P.java", "com/acme/b/d/Q.java"];
        string[] tree =
        [
            "a/src/main/java/com/acme/X.java",
            "a/src/main/java/com/acme/b/e/R.java",
            "b/src/main/java/com/acme/b/c/P.java",
            "b/src/main/java/com/acme/b/d/Q.java",
        ];

        var derived = CodeMappingDeriver.Derive(frames, tree);
        IReadOnlyList<CodeMapping> rules = [.. derived.Select(rule => Mapped(rule.StackRoot, rule.SourceRoot))];

        Assert.Equal(tree, frames.Select(frame => CodeMapper.Resolve(frame, rules)));
        // b's rule would take a's com/acme/b/e/R.java, so b is split by package when it is tried, rather
        // than taken whole and repaired file by file afterwards.
        Assert.Equal(
            [
                ("com/acme/", "a/src/main/java/com/acme/"),
                ("com/acme/b/c/", "b/src/main/java/com/acme/b/c/"),
                ("com/acme/b/d/", "b/src/main/java/com/acme/b/d/"),
            ],
            derived.Select(rule => (rule.StackRoot, rule.SourceRoot)).Order());
    }

    // A stack root another linked repository maps would tie with it, and a frame two repositories claim
    // links into neither, so the rule is not offered.
    [Fact]
    public void Suggest_calls_a_stack_root_another_repository_maps_a_conflict() =>
        Assert.Equal(
            MappingSuggestions.Conflicting,
            CodeMappingDeriver.Suggest(["src/app.ts"], ["src/app.ts"], [], [""]).Outcome);

    // The property every offered set must have: saved together, the rules send each sampled frame to the
    // file it matched or to nothing. tree[i] is the file frames[i] matches, so landing on another real
    // file (another module's copy of the same relative path) fails too.
    private static void AssertNoFrameIsMisrouted(string[] frames, string[] tree)
    {
        IReadOnlyList<CodeMapping> rules =
            [.. CodeMappingDeriver.Derive(frames, tree).Select(rule => Mapped(rule.StackRoot, rule.SourceRoot))];
        // Every frame here matched a file, so the longest one can always be carried: an empty answer
        // would satisfy the check below by mapping nothing.
        Assert.NotEmpty(rules);
        Assert.All(frames.Zip(tree), sample =>
        {
            if (CodeMapper.Resolve(sample.First, rules) is { } resolved)
            {
                Assert.Equal(sample.Second, resolved);
            }
        });
    }

    // A frame with no directory has nothing to narrow to; a rule from the empty root would rewrite every
    // frame the project sends, so it gets a rule of its own file.
    [Fact]
    public void A_frame_with_no_directory_gets_a_rule_of_its_own_file()
    {
        var rule = Assert.Single(CodeMappingDeriver.Derive(["Main.java", "static/js/app.js"], ["src/main/java/Main.java"]));

        Assert.Equal(("Main.java", "src/main/java/Main.java"), (rule.StackRoot, rule.SourceRoot));
        Assert.Null(CodeMapper.Resolve("static/js/app.js", [Mapped(rule.StackRoot, rule.SourceRoot)]));
    }

    // A Kotlin source set may leave out its root package (com/acme/web/Ctl.kt at src/main/kotlin/web/),
    // which derives com/acme/ → .../kotlin/, the same stack root the Java frames narrow to.
    [Fact]
    public void A_rule_with_a_stack_root_of_its_own_cannot_take_a_narrowed_rules_frames()
    {
        AssertNoFrameIsMisrouted(
            ["com/acme/web/Ctl.kt", "com/acme/data/Repo.kt", "com/acme/legacy/Old.java", "com/acme/legacy/Older.java", "com/acme/net/Http.java"],
            [
                "app/src/main/kotlin/web/Ctl.kt",
                "app/src/main/kotlin/data/Repo.kt",
                "app/src/main/java/com/acme/legacy/Old.java",
                "app/src/main/java/com/acme/legacy/Older.java",
                "app/src/main/java/com/acme/net/Http.java",
            ]);
    }

    // A longer stack root of its own would take part of a narrowed rule's frames.
    [Fact]
    public void A_longer_stack_root_cannot_take_part_of_a_narrowed_rules_frames() =>
        AssertNoFrameIsMisrouted(
            ["com/acme/a/A.java", "com/acme/b/B.java", "com/acme/b/Gen.kt"],
            ["src/main/java/com/acme/a/A.java", "src/main/java/com/acme/b/B.java", "build/Gen.kt"]);

    // A default-package frame narrows to the empty root, which the identity rule also holds.
    [Fact]
    public void The_identity_rule_and_a_default_package_rule_cannot_both_hold_the_empty_root() =>
        AssertNoFrameIsMisrouted(["Main.java", "README.java"], ["src/main/java/Main.java", "README.java"]);

    // d/a.js lives elsewhere than d/a.jsx and d/b.js, and a rule of its own file would also catch
    // d/a.jsx, which it prefixes. Nothing can carry d/a.js, so the rule that would misroute it is taken out
    // and the other two frames come back as rules of their own files.
    [Fact]
    public void Leaves_unmapped_a_frame_nothing_can_carry_and_keeps_the_others()
    {
        string[] frames = ["d/a.jsx", "d/b.js", "d/a.js"];
        string[] tree = ["lib/d/a.jsx", "lib/d/b.js", "src/d/a.js"];

        Assert.Equal(
            [("d/a.jsx", "lib/d/a.jsx"), ("d/b.js", "lib/d/b.js")],
            CodeMappingDeriver.Derive(frames, tree).Select(rule => (rule.StackRoot, rule.SourceRoot)).Order());
        AssertNoFrameIsMisrouted(frames, tree);
    }

    // A frame with no directory gets a rule of its own file, and when that rule is refused (X would also
    // catch X.kt, which the identity rule carries) it is left, not split by package: a whole path is not
    // a directory, and splitting it threw.
    [Theory]
    [InlineData(new[] { "X.kt", "X" }, new[] { "X.kt", "x/X" })]
    [InlineData(new[] { "Util.kts", "Util.kt" }, new[] { "Util.kts", "src/main/kotlin/Util.kt" })]
    public void A_refused_rule_of_one_file_is_left_rather_than_split(string[] frames, string[] tree) =>
        AssertNoFrameIsMisrouted(frames, tree);

    [Fact]
    public void Suggest_counts_a_rule_the_existing_mappings_already_apply_as_mapped() =>
        Assert.Equal(
            MappingSuggestions.AlreadyMapped,
            CodeMappingDeriver.Suggest(
                ["com/acme/billing/Invoice.java"], ["src/main/java/com/acme/billing/Invoice.java"],
                [Mapped("", "src/main/java/")], []).Outcome);

    private static CodeMapping Mapped(string stackRoot, string sourceRoot) =>
        new(Guid.NewGuid(), Guid.NewGuid(), stackRoot, sourceRoot);
}
