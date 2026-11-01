using System.Text;
using Condux.Core.SourceMaps;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// Reading a source map token by token, through the upload check and the read-time parse: nothing is built
/// for a field no map has, for sections no decode would use, or for the strings of a map nobody reads, however
/// many values they hold. The JSON is otherwise read as a document read it, so a repeated field takes its
/// last value, escaped separators count as what they spell, and anything after the map is refused. A string
/// a read would decode that is not valid text is malformed: refused at upload, and no map at read.
/// </summary>
public sealed class SourceMapJsonTests
{
    // A field of strings. Strings, because a string read out is a new allocation every time, where a
    // document's own index is rented from a pool and so cannot be counted reliably; a scan test
    // (SourceMapReadScanTest) holds that no document is built at all.
    private static readonly string Padding = "[" + string.Join(",", Enumerable.Repeat("\"padding\"", 200_000)) + "]";

    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void A_padded_map_is_checked_without_building_its_padding_or_its_sources()
    {
        // The check needs the version and the mappings' size, and confirms the strings a read would decode
        // are text without keeping them, so even the contents a map does carry are not built.
        var body = Utf8($$"""{"version":3,"x":{{Padding}},"sourcesContent":{{Padding}},"mappings":"AAAA"}""");

        var before = GC.GetAllocatedBytesForCurrentThread();
        var accepted = SourceMapContent.LooksLikeSourceMap(body);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(accepted);
        Assert.True(allocated < 64 * 1024, $"checking a {body.Length} byte map allocated {allocated} bytes");
    }

    [Fact]
    public void A_padded_map_is_read_without_building_its_padding()
    {
        var body = Utf8($$"""{"version":3,"x":{{Padding}},"sources":["app.ts"],"names":[],"mappings":"AAAA"}""");

        var before = GC.GetAllocatedBytesForCurrentThread();
        var map = SourceMap.Parse(body);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("app.ts", map!.OriginalPositionFor(0, 0)!.Source);
        Assert.True(allocated < 64 * 1024, $"reading a {body.Length} byte map allocated {allocated} bytes");
    }

    // A section list of entries no decode uses, and the strings of a map no decode reads.
    public static TheoryData<string> UnusedPadding => new()
    {
        $$"""{"version":3,"sections":[{{string.Join(",", Enumerable.Repeat("""{"offset":{},"map":{"names":["n"]}}""", 200_000))}}]}""",
        $$"""{"version":3,"sections":[],"names":{{Padding}}}""",
        $$"""{"version":3,"mappings":5,"sources":{{Padding}}}""",
    };

    [Theory]
    [MemberData(nameof(UnusedPadding))]
    public void What_no_decode_uses_is_passed_over_unbuilt(string json)
    {
        var body = Utf8(json);

        var before = GC.GetAllocatedBytesForCurrentThread();
        SourceMapContent.LooksLikeSourceMap(body);
        SourceMap.Parse(body);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 64 * 1024, $"a {body.Length} byte map allocated {allocated} bytes");
    }

    // Raw bytes, since a .NET string cannot hold invalid UTF-8 to be encoded from.
    public static TheoryData<byte[]> NotText => new()
    {
        Utf8("""{"version":3,"mappings":"AAAA","sources":["\ud800"]}"""),
        Utf8("""{"version":3,"mappings":"A\ud800AAA","sources":["a.ts"]}"""),
        Join(Utf8("{\"version\":3,\"mappings\":\"AA"), [0xC3, 0x28], Utf8("AA\",\"sources\":[\"a.ts\"]}")),
        Join(Utf8("{\"version\":3,\"mappings\":\"AAAA\",\"sources\":[\"a"), [0xFF], Utf8(".ts\"]}")),
        Join(Utf8("{\"version\":3,\"sections\":[{\"offset\":{},\"map\":{\"mappings\":\"AAAA\",\"names\":[\""),
            [0xED, 0xA0, 0x80], Utf8("\"]}}]}")),
    };

    private static byte[] Join(params byte[][] parts) => [.. parts.SelectMany(part => part)];

    [Theory]
    [MemberData(nameof(NotText))]
    public void A_string_a_read_would_decode_that_is_not_text_is_refused_and_read_as_no_map(byte[] body)
    {
        Assert.False(SourceMapContent.LooksLikeSourceMap(body));
        Assert.Null(SourceMap.Parse(body));
    }

    // A section no decode reads never has its sources, contents or names decoded, so one of them that is not
    // valid text costs nothing, as it cost nothing when a document held it.
    [Fact]
    public void A_section_no_decode_reads_is_not_held_to_its_strings()
    {
        var body = Utf8("""
            {"version":3,"sections":[
              {"offset":{"line":2},"map":{"mappings":"AAAA","sources":["kept.ts"]}},
              {"offset":{"line":1},"map":{"mappings":"AAAA","sources":["\ud800"]}}]}
            """);

        Assert.True(SourceMapContent.LooksLikeSourceMap(body));
        Assert.Equal("kept.ts", SourceMap.Parse(body)!.OriginalPositionFor(2, 0)!.Source);
    }

    // A property name that is not text (an escaped half of a surrogate pair) cannot be any field a map has,
    // so it is passed over like any other name, as a document passed over a name it never looked up. The
    // last one leaves its section with no mappings, so the index map has no usable section.
    [Theory]
    [InlineData("""{"version":3,"s\ud800":0,"mappings":"AAAA","sources":["a.ts"]}""", true)]
    [InlineData("""{"v\udc00":1,"version":3,"mappings":"AAAA","sources":["a.ts"]}""", true)]
    [InlineData("""{"version":3,"sections":[{"offset":{},"map":{"mapp\ud800ings":"AAAA"}}]}""", false)]
    public void A_property_name_that_is_not_text_is_passed_over(string json, bool readsAMap)
    {
        Assert.True(SourceMapContent.LooksLikeSourceMap(Utf8(json)));
        Assert.Equal(readsAMap, SourceMap.Parse(Utf8(json)) is not null);
    }

    // Passing such a name over costs nothing: the reader throws for each one it is asked to unescape, which
    // made a map of nothing else cost many times its size, in the root, a section or its offset alike.
    [Theory]
    [InlineData("""{"version":3,{0}"mappings":"AAAA","sources":["a.ts"]}""")]
    [InlineData("""{"version":3,"sections":[{"offset":{{0}"line":0},"map":{{0}"mappings":"AAAA"}}]}""")]
    public void Property_names_that_are_not_text_are_passed_over_unbuilt(string shape)
    {
        var names = string.Concat(Enumerable.Repeat("\"\\ud800\":0,", 200_000));
        var body = Utf8(shape.Replace("{0}", names));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var accepted = SourceMapContent.LooksLikeSourceMap(body);
        var map = SourceMap.Parse(body);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(accepted);
        Assert.NotNull(map);
        Assert.True(allocated < 64 * 1024, $"a {body.Length} byte map allocated {allocated} bytes");
    }

    // An escaped name is the field it spells, as a document read it, and an escape spelling anything else,
    // or a name longer than any field's, is no field.
    [Theory]
    [InlineData("""{"\u0076ersion":3,"\u006dappings":"AAAA","sour\u0063es":["a.ts"]}""", true)]
    [InlineData("""{"version":3,"\u006Dappings":"AAAA","sources":["a.ts"]}""", true)]
    [InlineData("""{"version":3,"\u00e9mappings":"AAAA","mappings\n":"AAAA","sources":["a.ts"]}""", false)]
    [InlineData("""{"version":3,"\u006dappings\u0041AAAAAAAAAAAAAAAAAAAAAAAA":"AAAA","sources":["a.ts"]}""", false)]
    public void An_escaped_name_is_the_field_it_spells(string json, bool readsAMap)
    {
        Assert.Equal(readsAMap, SourceMap.Parse(Utf8(json)) is not null);
    }

    // The last "sections" array is the one read, so an earlier one is never measured: its mappings may be
    // anything, and it costs nothing.
    [Fact]
    public void A_sections_array_a_later_key_replaces_is_not_held_to_anything()
    {
        var escaped = string.Concat(Enumerable.Repeat("\\u0041", 400_000));
        // A lone surrogate, a long escaped string and a raw byte that is not UTF-8, each in the replaced array.
        var body = Join(
            Utf8("{\"version\":3,\"sections\":[{\"offset\":{},\"map\":{\"mappings\":\"\\udc00\"}},"
                + "{\"offset\":{\"line\":1},\"map\":{\"mappings\":\"" + escaped + "\"}},"
                + "{\"offset\":{\"line\":2},\"map\":{\"mappings\":\""),
            [0xFF],
            Utf8("\"}}],\"sections\":[{\"offset\":{},\"map\":{\"mappings\":\"AAAA\",\"sources\":[\"kept.ts\"]}}]}"));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var accepted = SourceMapContent.LooksLikeSourceMap(body);
        var map = SourceMap.Parse(body);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(accepted);
        Assert.Equal("kept.ts", map!.OriginalPositionFor(0, 0)!.Source);
        Assert.True(allocated < 64 * 1024, $"a {body.Length} byte map allocated {allocated} bytes");
    }

    // A mappings string no decode reads is never measured, so it is held to nothing, as a document held it.
    // Each with the generated line where the map that is read begins.
    public static TheoryData<byte[], int> UnusedMappings => new()
    {
        { Join(Utf8("{\"version\":3,\"mappings\":\""), [0xFF], Utf8("\",\"mappings\":\"AAAA\",\"sources\":[\"kept.ts\"]}")), 0 },
        { Utf8("""{"version":3,"mappings":"\ud800","sections":[{"offset":{},"map":{"mappings":"AAAA","sources":["kept.ts"]}}]}"""), 0 },
        {
            Utf8("""
                {"version":3,"sections":[
                  {"offset":{"line":2},"map":{"mappings":"AAAA","sources":["kept.ts"]}},
                  {"offset":{"line":1},"map":{"mappings":"\udc00"}}]}
                """),
            2
        },
    };

    [Theory]
    [MemberData(nameof(UnusedMappings))]
    public void A_mappings_string_no_decode_reads_is_not_held_to_anything(byte[] body, int line)
    {
        Assert.True(SourceMapContent.LooksLikeSourceMap(body));
        Assert.Equal("kept.ts", SourceMap.Parse(body)!.OriginalPositionFor(line, 0)!.Source);
    }

    [Fact]
    public void An_unused_escaped_mappings_string_is_not_read()
    {
        var escaped = string.Concat(Enumerable.Repeat("\\u0041", 400_000));
        var body = Utf8($$"""{"version":3,"mappings":"{{escaped}}","sections":[]}""");

        var before = GC.GetAllocatedBytesForCurrentThread();
        SourceMapContent.LooksLikeSourceMap(body);
        SourceMap.Parse(body);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 64 * 1024, $"a {body.Length} byte map allocated {allocated} bytes");
    }

    // The last "sections" key is the one read, so repeating the key costs one plan however often it repeats.
    [Fact]
    public void A_repeated_sections_key_costs_one_plan()
    {
        var sections = "[" + string.Join(",", Enumerable.Repeat("""{"offset":{},"map":{"mappings":""}}""", 20_000)) + "]";
        var once = Utf8($$"""{"version":3,"sections":{{sections}}}""");
        var repeated = Utf8("{\"version\":3," + string.Join(",", Enumerable.Repeat($"\"sections\":{sections}", 20)) + "}");

        var onceAllocated = Allocated(() => SourceMapContent.LooksLikeSourceMap(once));
        var repeatedAllocated = Allocated(() => SourceMapContent.LooksLikeSourceMap(repeated));

        Assert.True(repeatedAllocated < 2 * onceAllocated,
            $"20 sections keys allocated {repeatedAllocated} bytes where one allocated {onceAllocated}");
        // Each key starts its plan again, so the repeats do not add up to more than the budget allows.
        Assert.True(SourceMapContent.LooksLikeSourceMap(repeated));
    }

    // Every section kept adds at least a line, so a plan is refused once it holds more sections than the line
    // budget, whatever the document holds after that, and what it costs stops growing there.
    [Fact]
    public void A_plan_stops_growing_at_the_line_budget()
    {
        var section = """{"offset":{},"map":{"mappings":""}}""";
        var atBudget = Utf8("{\"version\":3,\"sections\":[" + string.Join(",", Enumerable.Repeat(section, 100_001)) + "]}");
        var farPast = Utf8("{\"version\":3,\"sections\":[" + string.Join(",", Enumerable.Repeat(section, 400_000)) + "]}");

        var atBudgetAllocated = Allocated(() => SourceMapContent.LooksLikeSourceMap(atBudget));
        var farPastAllocated = Allocated(() => SourceMapContent.LooksLikeSourceMap(farPast));

        Assert.False(SourceMapContent.LooksLikeSourceMap(farPast));
        // A plan exactly at the budget still fits, and a section after it that no decode would use does not
        // count against it.
        var atBudgetThenSkipped = Utf8("{\"version\":3,\"sections\":["
            + string.Join(",", Enumerable.Repeat(section, 100_000)) + ",{\"offset\":{},\"map\":{}}]}");
        Assert.True(SourceMapContent.LooksLikeSourceMap(atBudgetThenSkipped));
        Assert.True(farPastAllocated < 2 * atBudgetAllocated,
            $"400,000 sections allocated {farPastAllocated} bytes where 100,001 allocated {atBudgetAllocated}");
    }

    private static long Allocated(Action read)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        read();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Theory]
    [InlineData("""{"version":2,"version":3,"mappings":"AAAA"}""", true)]
    [InlineData("""{"version":3,"version":2,"mappings":"AAAA"}""", false)]
    [InlineData("""{"version":3,"mappings":"AAAA","mappings":1}""", false)]
    [InlineData("""{"version":3,"mappings":1,"mappings":"AAAA"}""", true)]
    public void A_repeated_field_takes_its_last_value(string json, bool accepted) =>
        Assert.Equal(accepted, SourceMapContent.LooksLikeSourceMap(Utf8(json)));

    [Fact]
    public void A_repeated_field_is_read_with_its_last_value()
    {
        var map = SourceMap.Parse(Utf8("""{"sources":["first.ts"],"sources":["last.ts"],"mappings":"AAAA"}"""));

        Assert.Equal("last.ts", map!.OriginalPositionFor(0, 0)!.Source);
    }

    // Written with escapes, separators cost the decode what they spell, so the budget counts them so.
    [Fact]
    public void Escaped_separators_count_as_the_separators_they_spell()
    {
        var overBudget = string.Concat(Enumerable.Repeat("\\u003b", 100_000));
        var withinBudget = string.Concat(Enumerable.Repeat("\\u003b", 99_999));

        Assert.False(SourceMapContent.LooksLikeSourceMap(Utf8($$"""{"version":3,"mappings":"{{overBudget}}"}""")));
        Assert.True(SourceMapContent.LooksLikeSourceMap(Utf8($$"""{"version":3,"mappings":"{{withinBudget}}"}""")));
    }

    [Fact]
    public void Anything_after_the_map_is_refused()
    {
        var body = Utf8("""{"version":3,"mappings":"AAAA"} {}""");

        Assert.False(SourceMapContent.LooksLikeSourceMap(body));
        Assert.Null(SourceMap.Parse(body));
    }

    [Fact]
    public void Malformed_json_inside_a_field_no_map_has_is_still_refused()
    {
        var body = Utf8("""{"version":3,"x":[1,,2],"mappings":"AAAA"}""");

        Assert.False(SourceMapContent.LooksLikeSourceMap(body));
        Assert.Null(SourceMap.Parse(body));
    }
}
