using Condux.Core.DebugFiles;
using Condux.Symbolizer;
using Xunit;

namespace Condux.Symbolizer.Tests;

/// <summary>Reading llvm-symbolizer's JSON lines. The lines are its own output, captured from runs against
/// the fixture, so the shapes are the parser's and not ours.</summary>
public sealed class SymbolizerOutputTests
{
    [Fact]
    public void An_inline_chain_keeps_its_order_innermost_first()
    {
        const string line = """{"Address":"0x4600","ModuleName":"cart.so","Symbol":[{"Column":5,"Discriminator":0,"FileName":"/src/cart.c","FunctionName":"apply_discount","Line":5,"StartAddress":"","StartFileName":"/src/cart.c","StartLine":3},{"Column":10,"Discriminator":0,"FileName":"/src/cart.c","FunctionName":"cart_total","Line":11,"StartAddress":"0x45f0","StartFileName":"/src/cart.c","StartLine":10}]}""";

        var frames = SymbolizerOutput.ToFrames(line);

        Assert.Equal(
            [new ResolvedFrame("apply_discount", "/src/cart.c", 5, 5), new ResolvedFrame("cart_total", "/src/cart.c", 11, 10)],
            frames);
    }

    [Fact]
    public void The_parsers_placeholders_are_not_names()
    {
        const string line = """{"Address":"0x9","ModuleName":"cart.so","Symbol":[{"Column":0,"Discriminator":0,"FileName":"??","FunctionName":"??","Line":0,"StartAddress":"","StartFileName":"","StartLine":0}]}""";

        Assert.Empty(SymbolizerOutput.ToFrames(line));
    }

    [Fact]
    public void A_placeholder_beside_a_real_line_is_not_a_name()
    {
        // The line table knows the line but not the function, which the parser reports as "??".
        const string line = """{"Address":"0x9","Symbol":[{"FileName":"/src/cart.c","FunctionName":"??","Line":5,"Column":2,"StartLine":3}]}""";

        Assert.Equal([new ResolvedFrame(null, "/src/cart.c", 5, 2)], SymbolizerOutput.ToFrames(line));
    }

    // Each line below is the parser's own output for real code, captured from NDK r28 builds, and each
    // reports 0 for a line some rule might have used to tell DWARF from a guess.
    [Theory]
    // clang -O2: the instruction maps to line 0, the function is known.
    [InlineData("""{"Address":"0x4674","Symbol":[{"Column":3,"FileName":"/tmp/l0/t.c","FunctionName":"work","Line":0,"StartAddress":"0x4660","StartFileName":"/tmp/l0/t.c","StartLine":2}]}""", "work")]
    // -gline-tables-only: no declared line anywhere in the inline chain.
    [InlineData("""{"Address":"0x4580","Symbol":[{"Column":7,"FileName":"/tmp/c77r3/cart.c","FunctionName":"apply_discount","Line":4,"StartAddress":"","StartFileName":"","StartLine":0},{"Column":10,"FileName":"/tmp/c77r3/cart.c","FunctionName":"cart_total","Line":11,"StartAddress":"0x4578","StartFileName":"","StartLine":0}]}""", "apply_discount")]
    // A C++ static initialiser, which the compiler writes and gives no line at all.
    [InlineData("""{"Address":"0x4800","Symbol":[{"Column":0,"FileName":"/tmp/c77r3/g.cpp","FunctionName":"__cxx_global_var_init","Line":0,"StartAddress":"0x47fc","StartFileName":"","StartLine":0}]}""", "__cxx_global_var_init")]
    public void Real_code_reported_with_a_zero_line_is_kept(string line, string innermost)
    {
        Assert.Equal(innermost, SymbolizerOutput.ToFrames(line)[0].Function);
    }

    [Fact]
    public void A_function_with_no_file_is_kept()
    {
        const string line = """{"Address":"0x9","Symbol":[{"FileName":"","FunctionName":"start","Line":0,"Column":0,"StartLine":0}]}""";

        Assert.Equal([new ResolvedFrame("start", null, 0, 0)], SymbolizerOutput.ToFrames(line));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"Address":"0x9"}""")]
    [InlineData("""{"Symbol":"nope"}""")]
    public void A_line_that_is_not_what_was_asked_for_resolves_nothing(string line)
    {
        Assert.Empty(SymbolizerOutput.ToFrames(line));
    }
}
