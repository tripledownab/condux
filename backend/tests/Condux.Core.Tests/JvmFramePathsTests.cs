using Condux.Core.FixEngine;
using Condux.Core.Ingest;
using Condux.Core.Repos;
using Xunit;

namespace Condux.Core.Tests;

public class JvmFramePathsTests
{
    // Frames as the JVM SDK emitted them for an IndexOutOfBoundsException thrown from a class laid out like
    // spring-petclinic's Owner.getPet, captured by running EventPayload.exception on a real JDK, oldest
    // first. Stock Sentry Java SDKs build the same shape: module is the class, filename the bare file name.
    private const string PetclinicEvent = """
    {
      "event_id": "0a1b2c3d4e5f60718293a4b5c6d7e8f9",
      "timestamp": 1791554107.688,
      "platform": "java",
      "exception": { "values": [ {
        "type": "java.lang.IndexOutOfBoundsException",
        "value": "Index 6 out of bounds for length 0",
        "mechanism": { "type": "generic", "handled": false },
        "stacktrace": { "frames": [
          { "function": "getPet", "module": "org.springframework.samples.petclinic.owner.Owner", "filename": "Owner.java", "lineno": 5, "in_app": true },
          { "function": "get", "module": "java.util.ArrayList", "filename": "ArrayList.java", "lineno": 427, "in_app": false },
          { "function": "outOfBounds", "module": "jdk.internal.util.Preconditions", "filename": "Preconditions.java", "lineno": 100, "in_app": false }
        ] }
      } ] }
    }
    """;

    private const string OwnerPath = "org/springframework/samples/petclinic/owner/Owner.java";

    [Theory]
    [InlineData("Owner.java", "org.springframework.samples.petclinic.owner.Owner", OwnerPath)]
    [InlineData("Owner.java", "org.springframework.samples.petclinic.owner.Owner$Pet", OwnerPath)]
    [InlineData("Owners.kt", "com.example.owners.OwnersKt", "com/example/owners/Owners.kt")]
    [InlineData("Build.kts", "com.example.Build", "com/example/Build.kts")]
    [InlineData("Router.scala", "com.example.Router$", "com/example/Router.scala")]
    [InlineData("Task.groovy", "com.example.Task", "com/example/Task.groovy")]
    public void A_bare_jvm_file_name_takes_its_package_as_directory(string filename, string module, string expected) =>
        Assert.Equal(expected, JvmFramePaths.ToSourcePath(filename, module));

    [Theory]
    // Default package: there is no directory to add.
    [InlineData("Main.java", "Main")]
    // Already a path: whoever sent it knew better than the package.
    [InlineData("src/main/java/com/example/Owner.java", "com.example.Owner")]
    [InlineData(@"C:\src\Owner.java", "com.example.Owner")]
    // R8 or ProGuard output names no real file, so there is no source path to build.
    [InlineData("SourceFile", "a.b.c")]
    // Another runtime's dotted module is not a JVM package.
    [InlineData("views.py", "app.views")]
    [InlineData("checkout.js", "app.checkout")]
    // Nothing to build from, or a module no class name could be.
    [InlineData("Owner.java", null)]
    [InlineData("Owner.java", "")]
    [InlineData("Owner.java", ".Owner")]
    [InlineData("Owner.java", "com..example.Owner")]
    [InlineData("Owner.java", "com.example.")]
    [InlineData("ArrayList.java", "java.base/java.util.ArrayList")]
    [InlineData("Owner.java", @"com\example.Owner")]
    [InlineData(null, "com.example.Owner")]
    public void Anything_else_keeps_its_file_name(string? filename, string? module) =>
        Assert.Equal(filename, JvmFramePaths.ToSourcePath(filename, module));

    [Fact]
    public void The_parser_stores_a_jvm_frame_under_its_package_path_and_keeps_the_class()
    {
        var frames = SentryParser.ParseStore(PetclinicEvent).Event!.Exceptions[0].Stacktrace!.Frames;

        Assert.Equal(OwnerPath, frames[0].Filename);
        Assert.Equal("org.springframework.samples.petclinic.owner.Owner", frames[0].Module);
        Assert.Equal("java/util/ArrayList.java", frames[1].Filename);
    }

    [Fact]
    public void One_source_root_mapping_scopes_a_fix_to_the_culprit_class_file()
    {
        var e = SentryParser.ParseStore(PetclinicEvent).Event!;
        IReadOnlyList<CodeMapping> mappings = [new(Guid.NewGuid(), Guid.NewGuid(), "org/", "src/main/java/org/")];

        Assert.Equal(["src/main/java/" + OwnerPath], FixContextAssembler.Assemble(e, mappings).ScopedPaths);
        Assert.Equal("src/main/java/" + OwnerPath, FixContextAssembler.CulpritPath(e, mappings));
    }

    [Fact]
    public void Suggest_from_errors_derives_the_source_root_from_a_jvm_frame()
    {
        var e = SentryParser.ParseStore(PetclinicEvent).Event!;
        var inApp = e.Exceptions[0].Stacktrace!.Frames.Where(f => f.InApp).Select(f => f.Filename!);

        var rule = CodeMappingDeriver.Derive(inApp, ["src/main/java/" + OwnerPath, "pom.xml"])[0];

        // Every package sits under the one source root, so the rule adds it in front of the package its
        // frames share, rather than rewriting every path from the empty root.
        var package = OwnerPath[..(OwnerPath.LastIndexOf('/') + 1)];
        Assert.Equal((package, "src/main/java/" + package), (rule.StackRoot, rule.SourceRoot));
        var mapping = new CodeMapping(Guid.NewGuid(), Guid.NewGuid(), rule.StackRoot, rule.SourceRoot);
        Assert.Equal("src/main/java/" + OwnerPath, CodeMapper.Resolve(OwnerPath, [mapping]));
    }
}
