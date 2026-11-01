using System.Text;
using Condux.Core.DebugFiles;
using Condux.Core.Events;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// The map is shaped like the file Flutter's <c>--save-obfuscation-map</c> writes (Flutter's own
/// obfuscation docs: a JSON array of original then obfuscated names), with framework classes in it, which
/// is what makes a word inside a longer string unsafe to restore.
/// </summary>
public class DartObfuscationMapsTests
{
    private static IReadOnlyDictionary<string, string> Map(string json)
    {
        var (map, rejection) = DartObfuscationMaps.ToMap(Encoding.UTF8.GetBytes(json));
        Assert.Null(rejection);
        return map!;
    }

    [Fact]
    public void A_map_is_keyed_by_obfuscated_name_and_a_later_pair_wins()
    {
        var map = Map("""["MaterialApp","ex","CartException","xY","PaymentError","xY"]""");

        Assert.Equal("MaterialApp", map["ex"]);
        Assert.Equal("PaymentError", map["xY"]);
        Assert.Equal(2, map.Count);
    }

    [Theory]
    [InlineData("""{"ex":"MaterialApp"}""", "JSON array")]
    [InlineData("""["MaterialApp","ex","Scaffold"]""", "odd number")]
    [InlineData("""["MaterialApp",""]""", "empty, null")]
    [InlineData("""["MaterialApp",null]""", "empty, null")]
    // Postgres text holds no NUL, so storing one failed the upload with a 500.
    [InlineData("""["Material\u0000App","ex"]""", "NUL")]
    [InlineData("""["MaterialApp","e\u0000x"]""", "NUL")]
    [InlineData("""[1,2]""", "JSON array")]
    [InlineData("not json", "JSON array")]
    public void A_body_that_is_not_a_map_is_refused_with_the_reason(string body, string reason)
    {
        var (map, rejection) = DartObfuscationMaps.ToMap(Encoding.UTF8.GetBytes(body));

        Assert.Null(map);
        Assert.Contains(reason, rejection);
    }

    [Fact]
    public void A_name_past_the_cap_on_either_side_of_a_pair_is_refused()
    {
        var longest = new string('L', DartObfuscationMaps.MaxNameLength);
        var tooLong = longest + "L";

        Assert.NotNull(DartObfuscationMaps.ToMap(Encoding.UTF8.GetBytes($"""["{longest}","xY"]""")).Map);
        Assert.Contains("longer than",
            DartObfuscationMaps.ToMap(Encoding.UTF8.GetBytes($"""["{tooLong}","xY"]""")).Rejection);
        Assert.Contains("longer than",
            DartObfuscationMaps.ToMap(Encoding.UTF8.GetBytes($"""["CartException","{tooLong}"]""")).Rejection);
    }

    [Fact]
    public void An_exception_type_and_each_instance_of_in_its_message_are_restored()
    {
        var map = Map("""["CartException","xY","LineItem","aB","Error","in"]""");
        var e = Crash(new ExceptionValue { Type = "xY", Value = "Instance of 'xY' about Instance of 'aB'" });

        var restored = DartObfuscationMaps.Restore(e, Lookup(e, map));

        var ex = Assert.Single(restored.Exceptions);
        Assert.Equal("CartException", ex.Type);
        Assert.Equal("Instance of 'CartException' about Instance of 'LineItem'", ex.Value);
    }

    [Fact]
    public void A_message_has_at_most_the_cap_of_names_put_back_however_often_one_repeats()
    {
        var map = Map("""["CartException","xY"]""");
        var repeated = string.Concat(Enumerable.Repeat("Instance of 'xY' ", 1000));
        var e = Crash(new ExceptionValue { Type = "StateError", Value = repeated });

        var value = Assert.Single(DartObfuscationMaps.Restore(e, Lookup(e, map)).Exceptions).Value!;

        Assert.Equal(DartObfuscationMaps.MaxNamesPerEvent, CountOf(value, "Instance of 'CartException'"));
        Assert.Equal(1000 - DartObfuscationMaps.MaxNamesPerEvent, CountOf(value, "Instance of 'xY'"));
    }

    [Fact]
    public void An_event_has_at_most_the_cap_of_names_put_back_across_all_its_exceptions()
    {
        // The cap is per event: repeating an exception must not repeat the allowance.
        var map = Map("""["CartException","xY"]""");
        var exceptions = Enumerable.Range(0, 100)
            .Select(_ => new ExceptionValue { Type = "xY", Value = "Instance of 'xY'" }).ToArray();
        var e = Crash(exceptions);

        var restored = DartObfuscationMaps.Restore(e, Lookup(e, map));

        var put = restored.Exceptions.Sum(ex => (ex.Type == "CartException" ? 1 : 0)
            + CountOf(ex.Value!, "Instance of 'CartException'"));
        Assert.Equal(DartObfuscationMaps.MaxNamesPerEvent, put);
    }

    [Fact]
    public void The_budget_is_spent_on_the_last_exception_first()
    {
        // The issue page shows the last exception, so an earlier one full of names must not use it all up.
        var map = Map("""["CartException","xY"]""");
        var earlier = new ExceptionValue
        {
            Type = "StateError",
            Value = string.Concat(Enumerable.Repeat("Instance of 'xY' ", 100)),
        };
        var last = new ExceptionValue { Type = "xY", Value = "Instance of 'xY'" };
        var e = Crash(earlier, last);

        var restored = DartObfuscationMaps.Restore(e, Lookup(e, map));

        Assert.Equal("CartException", restored.Exceptions[1].Type);
        Assert.Equal("Instance of 'CartException'", restored.Exceptions[1].Value);
        Assert.Equal(DartObfuscationMaps.MaxNamesPerEvent - 2,
            CountOf(restored.Exceptions[0].Value!, "Instance of 'CartException'"));
    }

    [Fact]
    public void Debug_ids_come_from_the_images_the_frames_ran_in_first_and_only_native_kinds()
    {
        // The app's image is last of many, as when an SDK lists every image the process loaded; its frame
        // puts it first. A source map image is never a debug file, so it is never looked up, even listed first.
        var app = new DebugImage
        {
            Type = "elf",
            DebugId = "AAAAAAAA-0000-0000-0000-000000000001",
            ImageAddr = "0x7000000000",
            ImageSize = 0x100000,
        };
        var images = Enumerable.Range(0, 40).Select(i => new DebugImage
        {
            Type = "elf",
            DebugId = $"bbbbbbbb-0000-0000-0000-{i:x12}",
            ImageAddr = $"0x{0x1000000000L + i * 0x1000000L:x}",
            ImageSize = 0x1000,
        }).Prepend(new DebugImage { Type = "sourcemap", DebugId = "cccccccc-0000-0000-0000-000000000001" })
            .Append(app).ToList();
        var e = new Event
        {
            DebugImages = images,
            Exceptions =
            [
                new ExceptionValue
                {
                    Type = "xY",
                    Stacktrace = new Stacktrace { Frames = [new Frame { InstructionAddr = "0x7000000010" }] },
                },
            ],
        };

        var ids = DartObfuscationMaps.ToDebugIds(e);

        Assert.Equal("aaaaaaaa-0000-0000-0000-000000000001", ids[0]);
        Assert.Equal(DartObfuscationMaps.MaxImagesLookedUp, ids.Count);
        Assert.DoesNotContain("cccccccc-0000-0000-0000-000000000001", ids);
    }

    [Fact]
    public void A_word_inside_a_longer_type_or_message_is_left_alone()
    {
        // The framework fills the short names, so "in" is a key; only a whole type is restored.
        var map = Map("""["Error","in","CartException","xY"]""");
        var e = Crash(new ExceptionValue { Type = "Bloc xY", Value = "failed in checkout" });

        Assert.Same(e, DartObfuscationMaps.Restore(e, Lookup(e, map)));
    }

    [Fact]
    public void An_event_with_nothing_the_map_holds_is_returned_as_it_was()
    {
        var map = Map("""["CartException","xY"]""");
        var e = Crash(new ExceptionValue { Type = "StateError", Value = "Bad state: no element" });

        Assert.Same(e, DartObfuscationMaps.Restore(e, Lookup(e, map)));
    }

    [Fact]
    public void Names_come_from_the_last_exception_first_and_stop_at_the_cap()
    {
        var many = string.Join(' ', Enumerable.Range(0, 100).Select(i => $"Instance of 'n{i}'"));
        var e = Crash(
            new ExceptionValue { Type = "first", Value = many },
            new ExceptionValue { Type = "last" });

        var names = DartObfuscationMaps.ToNames(e);

        Assert.Equal(DartObfuscationMaps.MaxNamesPerEvent, names.Count);
        Assert.Equal(["last", "first", "n0"], names.Take(3));
    }

    [Fact]
    public void A_name_past_the_length_cap_is_not_looked_up()
    {
        var e = Crash(new ExceptionValue { Type = new string('x', DartObfuscationMaps.MaxNameLength + 1) });

        Assert.Empty(DartObfuscationMaps.ToNames(e));
    }

    // What the database answers: the names the event carries that the map holds.
    private static Dictionary<string, string> Lookup(Event e, IReadOnlyDictionary<string, string> map) =>
        DartObfuscationMaps.ToNames(e).Where(map.ContainsKey).ToDictionary(n => n, n => map[n]);

    private static Event Crash(params ExceptionValue[] exceptions) => new() { Exceptions = exceptions };

    private static int CountOf(string text, string part) =>
        (text.Length - text.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;
}
