extern alias symbolizer;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.DebugFiles;
using Condux.Core.Events;
using Condux.Core.Grouping;
using Condux.Core.Ingest;
using Condux.Core.Plans;
using Condux.Core.SourceMaps;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.ClickHouse;
using Condux.Storage.DebugFiles;
using Condux.Storage.Postgres;
using Condux.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Condux.Tests.Shared.ElfFiles;

namespace Condux.IntegrationTests;

/// <summary>
/// Read-time native symbolication end to end (ADR-0049): a real Flutter debug file goes in through the CI
/// upload, a real sentry_flutter crash is stored, and the issue page reads its address-only frames back as
/// Dart source. The symbolizer is the real service with the real llvm-symbolizer, hosted in process, so it
/// needs the binary and fails rather than skips without one. Around that: what the read does when the
/// symbolizer is down or no file was uploaded, a file from before hashes were recorded, the order of an
/// inline chain, and the cap on how many images one event may make it look up.
/// </summary>
[Trait("Category", "Integration")]
public sealed class NativeSymbolicationApiTest : IClassFixture<PostgresFixture>, IClassFixture<ClickHouseFixture>, IDisposable
{
    private const string Dart = "/private/tmp/crashapp-src/crashapp/lib/main.dart";

    // Where the macOS probe's own source sat when it was built, with the build machine's directories
    // rewritten to neutral ones of the same length (Golden/README.md).
    private const string Probe = "/private/var/folders/xx/xxxxxxxxxxxxxxxxxxxxxxxxxxxxxx/T/build.xxxxxxxxxx/probe/lib/main.dart";

    private readonly PostgresFixture _pg;
    private readonly ClickHouseFixture _ch;
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "native-pass-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<symbolizer::Program> _symbolizer;

    public NativeSymbolicationApiTest(PostgresFixture pg, ClickHouseFixture ch)
    {
        (_pg, _ch) = (pg, ch);
        var binary = SymbolizerBinary.Locate();
        _symbolizer = new WebApplicationFactory<symbolizer::Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("CONDUX_LLVM_SYMBOLIZER", binary);
            b.UseSetting("CONDUX_SYMBOLIZER_CACHE_DIR", _cache);
        });
    }

    public void Dispose()
    {
        _symbolizer.Dispose();
        if (Directory.Exists(_cache))
        {
            Directory.Delete(_cache, recursive: true);
        }
    }

    private static byte[] Golden(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", name));

    private static Event FlutterCrash() =>
        SentryParser.ParseEnvelope(Golden("flutter-crashapp-envelope.txt")).Event!;

    [Fact]
    public async Task The_issue_page_reads_an_obfuscated_flutter_crash_as_its_dart_source()
    {
        var (app, store) = App();
        var (admin, project) = await ProvisionAsync(app);
        await UploadAsync(app, admin, project, Golden("flutter-crashapp.symbols"));
        await SeedAsync(project, FlutterCrash());

        var frames = await ReadFramesAsync(admin, project);

        Assert.Equal(
            [
                ("main", Dart, 36, 5), (null, null, 0, 0), ("checkout", Dart, 21, 3), (null, null, 0, 0),
                ("CartService.total", Dart, 8, 12), ("CartService.applyDiscount", Dart, 13, 7),
            ],
            frames.Select(f => (f.Function, f.Filename, f.Lineno, f.Colno)));
        // The SDK sends no in_app for these frames; the resolved path is what says which are the app's.
        Assert.Equal([true, false, true, false, true, true], frames.Select(f => f.InApp));
        Assert.Single(store.Objects);
    }

    [Fact]
    public async Task The_issue_page_reads_an_apple_flutter_crash_through_its_dsym()
    {
        // A real crash from an obfuscated Flutter build on macOS, sent by sentry-cocoa as it is on iOS: the
        // app image is Mach-O and named by its LC_UUID. The dSYM's arm64 slice carries that UUID.
        var (app, _) = App();
        var (admin, project) = await ProvisionAsync(app);
        await UploadAsync(app, admin, project, Golden("flutter-probe-macos-arm64.dwarf"));
        await SeedAsync(project, SentryParser.ParseEnvelope(Golden("flutter-probe-macos-envelope.txt")).Event!);

        var frames = await ReadFramesAsync(admin, project);

        // The private class keeps its leading underscore: llvm-symbolizer drops it from Mach-O names unless
        // asked for the name the DWARF writes.
        Assert.Equal(
            [
                ("_ProbeAppState._run", Probe, 48, 7), (null, null, 0, 0), ("probeLevelOne", Probe, 20, 3),
                ("probeLevelTwo", Probe, 15, 30), ("probeLevelThree", Probe, 11, 14),
            ],
            frames.Select(f => (f.Function, f.Filename, f.Lineno, f.Colno)));
        Assert.Equal([true, false, true, true, true], frames.Select(f => f.InApp));
    }

    [Fact]
    public async Task A_symbolizer_that_is_down_leaves_the_frames_as_addresses()
    {
        var (app, _) = App(new FailingHandler());
        var (admin, project) = await ProvisionAsync(app);
        await UploadAsync(app, admin, project, Golden("flutter-crashapp.symbols"));
        var crash = FlutterCrash();
        await SeedAsync(project, crash);

        var frames = await ReadFramesAsync(admin, project);

        Assert.All(frames, f => Assert.Null(f.Function));
        Assert.Equal(
            crash.Exceptions[^1].Stacktrace!.Frames.Select(f => f.InstructionAddr),
            frames.Select(f => f.InstructionAddr));
    }

    [Fact]
    public async Task A_crash_with_no_uploaded_file_leaves_the_frames_as_addresses()
    {
        var (app, _) = App();
        var (admin, project) = await ProvisionAsync(app);
        await SeedAsync(project, FlutterCrash());

        var frames = await ReadFramesAsync(admin, project);

        Assert.All(frames, f => Assert.Null(f.Function));
    }

    [Fact]
    public async Task A_file_uploaded_before_hashes_were_recorded_is_hashed_once_and_resolves()
    {
        var (app, _) = App();
        var (admin, project) = await ProvisionAsync(app);
        var symbols = Golden("flutter-crashapp.symbols");
        await UploadAsync(app, admin, project, symbols);
        await ExecuteAsync("UPDATE debug_files SET sha256 = NULL WHERE project_id = @p", project);
        await SeedAsync(project, FlutterCrash());

        var frames = await ReadFramesAsync(admin, project);

        Assert.Equal("CartService.applyDiscount", frames[^1].Function);
        var found = await new DebugFileRepository(_pg.ConnectionString)
            .FindAsync(project, ["30f93e7a-2768-18da-3fa3-6f07eecef2fb"]);
        Assert.Equal(DebugFileHashes.ToSha256(symbols), Assert.Single(found).Value.Sha256);
    }

    [Fact]
    public async Task An_inlined_call_reads_as_its_caller_then_itself_oldest_first()
    {
        // The C fixture's trap is apply_discount inlined into cart_total. The symbolizer answers innermost
        // first; the stack reads oldest first, so the inlined function has to come out last.
        var (app, _) = App();
        var (admin, project) = await ProvisionAsync(app);
        var cart = Golden("cart.so");
        await UploadAsync(app, admin, project, cart);
        var debugId = ElfDebugFile.ToBuild(cart).Build!.DebugId;
        // Offset 0 sits in the image but names nothing, so its frame has to stay rather than vanish.
        await SeedAsync(project, NativeEvent([(debugId, 0x1000_0000UL)], [0x1000_0000UL, 0x1000_0000UL + 0x4600]));

        var frames = await ReadFramesAsync(admin, project);

        Assert.Equal([null, "cart_total", "apply_discount"], frames.Select(f => f.Function));
        Assert.Equal(["0x10000000", "0x10004600", "0x10004600"], frames.Select(f => f.InstructionAddr));
        Assert.Equal([false, false, true], frames.Select(f => f.Inlined));
        // A C source path says nothing about in_app, so the frames keep what the SDK sent.
        Assert.All(frames, f => Assert.False(f.InApp));
    }

    [Fact]
    public async Task What_resolving_writes_into_one_event_is_bounded_and_spent_nearest_the_crash()
    {
        // One address repeated, and an answer four frames deep with long names for it: every copy would get
        // the whole chain, so the sender's repeat count times the file's name length would size the event.
        const int Depth = 4;
        var recording = new RecordingHandler(new DeepChainHandler(_symbolizer.Server.CreateHandler(), Depth));
        var (app, _) = App(recording);
        var (admin, project) = await ProvisionAsync(app);
        var cart = Golden("cart.so");
        await UploadAsync(app, admin, project, cart);
        const int Copies = 600;
        await SeedAsync(project, NativeEvent(
            [(ElfDebugFile.ToBuild(cart).Build!.DebugId, 0x1000_0000UL)],
            [.. Enumerable.Repeat(0x1000_4600UL, Copies)]));

        var frames = await ReadFramesAsync(admin, project);

        // The symbolizer is asked once per address, however often the stack repeats it.
        Assert.Single(Assert.Single(recording.Resolved).Addresses);
        var written = frames.Where(f => f.Function is not null).ToList();
        Assert.InRange(
            written.Sum(f => JsonSerializer.SerializeToUtf8Bytes(f).Length), 1, NativeSymbolicator.MaxWrittenBytes);
        Assert.Equal(["f3", "f2", "f1", "f0"], frames.TakeLast(Depth).Select(f => f.Function![..2]));
        // Past the budget the oldest copies stay as addresses, and no copy is written in part: one cut short
        // would leave frames marked inlined into a function that is not there.
        Assert.Null(frames[0].Function);
        Assert.Equal(
            frames.Count(f => f.Function?.StartsWith("f0", StringComparison.Ordinal) == true),
            frames.Count(f => f.Function?.StartsWith("f3", StringComparison.Ordinal) == true));
        Assert.Equal("0x10004600", frames[0].InstructionAddr);
    }

    [Fact]
    public async Task One_event_makes_the_symbolizer_look_at_four_images_at_most_those_nearest_the_crash()
    {
        // Five images, each with an uploaded file and one frame at its own offset. The sender chooses how
        // many images an event lists, so the pass resolves a fixed number, taken from the crash site back.
        var recording = new RecordingHandler(_symbolizer.Server.CreateHandler());
        var (app, _) = App(recording);
        var (admin, project) = await ProvisionAsync(app);
        var images = new List<(string DebugId, ulong Base)>();
        for (var i = 0; i < 5; i++)
        {
            var file = SymbolsFile($"{i:x2}000000000000000000000000000000");
            await UploadAsync(app, admin, project, file);
            images.Add((ElfDebugFile.ToBuild(file).Build!.DebugId, (ulong)(i + 1) << 32));
        }
        var offsets = Enumerable.Range(1, 5).Select(i => (ulong)i * 0x10).ToList();
        await SeedAsync(project, NativeEvent(images, [.. images.Select((image, i) => image.Base + offsets[i])]));

        await ReadFramesAsync(admin, project);

        Assert.Equal(4, recording.Resolved.Count);
        // The oldest frame's image is the one left out.
        Assert.DoesNotContain("0x10", recording.Resolved.SelectMany(r => r.Addresses));
    }

    [Fact]
    public async Task Images_with_no_uploaded_file_do_not_take_the_places_of_one_that_has_a_file()
    {
        // A native crash usually stops in system libraries nobody uploads files for, nearest the crash; the
        // app's own image, further down, is the one with a file and the one to resolve.
        var recording = new RecordingHandler(_symbolizer.Server.CreateHandler());
        var (app, _) = App(recording);
        var (admin, project) = await ProvisionAsync(app);
        var cart = Golden("cart.so");
        await UploadAsync(app, admin, project, cart);
        var images = new List<(string DebugId, ulong Base)> { (ElfDebugFile.ToBuild(cart).Build!.DebugId, 1UL << 32) };
        for (var i = 0; i < NativeSymbolicator.MaxImages + 2; i++)
        {
            images.Add((Guid.NewGuid().ToString(), (ulong)(i + 2) << 32));
        }
        // The app's frame is the oldest; every system image sits between it and the crash.
        await SeedAsync(project, NativeEvent(images, [.. images.Select((image, i) => image.Base + (i == 0 ? 0x4600UL : 0x10UL))]));

        var frames = await ReadFramesAsync(admin, project);

        Assert.Equal(DebugFileHashes.ToSha256(cart), Assert.Single(recording.Resolved).File);
        Assert.Equal(["cart_total", "apply_discount"], frames.Take(2).Select(f => f.Function));
    }

    [Fact]
    public async Task A_page_of_events_shares_one_allowance_of_bytes_written()
    {
        const int Depth = 4;
        var (app, _) = App(new DeepChainHandler(_symbolizer.Server.CreateHandler(), Depth));
        var (admin, project) = await ProvisionAsync(app);
        var cart = Golden("cart.so");
        await UploadAsync(app, admin, project, cart);
        var crash = NativeEvent(
            [(ElfDebugFile.ToBuild(cart).Build!.DebugId, 0x1000_0000UL)], [.. Enumerable.Repeat(0x1000_4600UL, 600)]);
        await SeedAsync(project, crash);
        await SeedAsync(project, crash with { EventId = Guid.NewGuid().ToString("N") });

        var page = await ReadPageFramesAsync(admin, project, limit: 2);

        Assert.Equal(2, page.Count);
        var written = page.SelectMany(frames => frames).Where(f => f.Function is not null)
            .Sum(f => JsonSerializer.SerializeToUtf8Bytes(f).Length);
        Assert.InRange(written, 1, NativeSymbolicator.MaxWrittenBytes);
    }

    [Fact]
    public async Task One_image_takes_no_more_offsets_than_a_batch_and_keeps_those_nearest_the_crash()
    {
        var recording = new RecordingHandler(_symbolizer.Server.CreateHandler());
        var (app, _) = App(recording);
        var (admin, project) = await ProvisionAsync(app);
        var cart = Golden("cart.so");
        await UploadAsync(app, admin, project, cart);
        const ulong Base = 0x1000_0000UL;
        var addresses = Enumerable.Range(0, NativeSymbolicator.MaxOffsetsPerImage + 88)
            .Select(i => Base + 0x4000 + (ulong)i * 4).ToList();
        await SeedAsync(project, NativeEvent([(ElfDebugFile.ToBuild(cart).Build!.DebugId, Base)], addresses));

        await ReadFramesAsync(admin, project);

        var sent = Assert.Single(recording.Resolved).Addresses;
        Assert.Equal(NativeSymbolicator.MaxOffsetsPerImage, sent.Count);
        Assert.Contains($"0x{addresses[^1] - Base:x}", sent);
        Assert.DoesNotContain($"0x{addresses[0] - Base:x}", sent);
    }

    [Fact]
    public async Task Of_two_builds_sharing_a_debug_id_the_newest_upload_answers()
    {
        // A debug id is the first 16 bytes of the build ID, so two longer build IDs can share one. The
        // newer upload is the build a fresh crash came from.
        var recording = new RecordingHandler(_symbolizer.Server.CreateHandler());
        var (app, _) = App(recording);
        var (admin, project) = await ProvisionAsync(app);
        var older = SymbolsFile("cd00000000000000000000000000000000000001");
        var newer = SymbolsFile("cd00000000000000000000000000000000000002");
        await UploadAsync(app, admin, project, older);
        await UploadAsync(app, admin, project, newer);
        var debugId = ElfDebugFile.ToBuild(newer).Build!.DebugId;
        Assert.Equal(ElfDebugFile.ToBuild(older).Build!.DebugId, debugId);
        await SeedAsync(project, NativeEvent([(debugId, 0x1000_0000UL)], [0x1000_0010UL]));

        await ReadFramesAsync(admin, project);

        Assert.Equal(DebugFileHashes.ToSha256(newer), Assert.Single(recording.Resolved).File);
    }

    [Fact]
    public async Task An_upload_again_makes_that_build_the_newest()
    {
        var recording = new RecordingHandler(_symbolizer.Server.CreateHandler());
        var (app, _) = App(recording);
        var (admin, project) = await ProvisionAsync(app);
        var first = SymbolsFile("ce00000000000000000000000000000000000001");
        var second = SymbolsFile("ce00000000000000000000000000000000000002");
        await UploadAsync(app, admin, project, first);
        await UploadAsync(app, admin, project, second);
        await UploadAsync(app, admin, project, first);
        await SeedAsync(project, NativeEvent([(ElfDebugFile.ToBuild(first).Build!.DebugId, 0x1000_0000UL)], [0x1000_0010UL]));

        await ReadFramesAsync(admin, project);

        Assert.Equal(DebugFileHashes.ToSha256(first), Assert.Single(recording.Resolved).File);
    }

    [Fact]
    public async Task A_frame_that_already_has_a_function_is_left_as_it_is()
    {
        var (app, _) = App();
        var (admin, project) = await ProvisionAsync(app);
        var cart = Golden("cart.so");
        await UploadAsync(app, admin, project, cart);
        var crash = NativeEvent([(ElfDebugFile.ToBuild(cart).Build!.DebugId, 0x1000_0000UL)], [0x1000_4600UL]);
        var named = crash with
        {
            Exceptions = [crash.Exceptions[0] with
            {
                Stacktrace = new Stacktrace
                {
                    Frames = [crash.Exceptions[0].Stacktrace!.Frames[0] with { Function = "already_named" }],
                },
            }],
        };
        await SeedAsync(project, named);

        var frames = await ReadFramesAsync(admin, project);

        Assert.Equal("already_named", Assert.Single(frames).Function);
    }

    [Theory]
    [InlineData("filename")]
    [InlineData("module")]
    public async Task A_frame_grouping_keys_by_name_is_left_as_it_is(string named)
    {
        var (app, _) = App();
        var (admin, project) = await ProvisionAsync(app);
        var cart = Golden("cart.so");
        await UploadAsync(app, admin, project, cart);
        var crash = NativeEvent([(ElfDebugFile.ToBuild(cart).Build!.DebugId, 0x1000_0000UL)], [0x1000_4600UL]);
        var frame = crash.Exceptions[0].Stacktrace!.Frames[0];
        frame = named == "filename" ? frame with { Filename = "cart.c" } : frame with { Module = "cart" };
        await SeedAsync(project, crash with
        {
            Exceptions = [crash.Exceptions[0] with { Stacktrace = new Stacktrace { Frames = [frame] } }],
        });

        var frames = await ReadFramesAsync(admin, project);

        Assert.Null(Assert.Single(frames).Function);
    }

    [Fact]
    public async Task An_address_past_its_image_or_in_an_image_of_no_size_is_not_sent()
    {
        // The symbolizer names an address past the image after some symbol of the file, which its answer
        // cannot tell from a real frame.
        var recording = new RecordingHandler(_symbolizer.Server.CreateHandler());
        var (app, _) = App(recording);
        var (admin, project) = await ProvisionAsync(app);
        var cart = Golden("cart.so");
        await UploadAsync(app, admin, project, cart);
        var debugId = ElfDebugFile.ToBuild(cart).Build!.DebugId;
        var crash = NativeEvent([(debugId, 0x1000_0000UL)], [0x1000_0000UL + 0x100000UL + 0x10]);
        var past = crash with
        {
            Exceptions = [crash.Exceptions[0] with
            {
                Stacktrace = new Stacktrace
                {
                    Frames = [crash.Exceptions[0].Stacktrace!.Frames[0] with { ImageAddr = "0x10000000" }],
                },
            }],
        };
        await SeedAsync(project, past);
        await ReadFramesAsync(admin, project);

        var (unsizedApp, _) = App(recording);
        var (unsizedAdmin, unsizedProject) = await ProvisionAsync(unsizedApp);
        await UploadAsync(unsizedApp, unsizedAdmin, unsizedProject, cart);
        var unsized = NativeEvent([(debugId, 0x1000_0000UL)], [0x1000_4600UL]);
        await SeedAsync(unsizedProject, unsized with
        {
            DebugImages = [unsized.DebugImages[0] with { ImageSize = null }],
        });
        await ReadFramesAsync(unsizedAdmin, unsizedProject);

        Assert.Empty(recording.Resolved);
    }

    [Fact]
    public async Task A_read_finding_every_file_read_in_use_leaves_the_frames_and_resolves_once_one_is_free()
    {
        var (app, _) = App();
        var (admin, project) = await ProvisionAsync(app);
        var cart = Golden("cart.so");
        await UploadAsync(app, admin, project, cart);
        await SeedAsync(project, NativeEvent([(ElfDebugFile.ToBuild(cart).Build!.DebugId, 0x1000_0000UL)], [0x1000_4600UL]));
        var gate = app.Services.GetRequiredService<DebugFileReadGate>();
        Assert.True(gate.TryEnter());
        Assert.True(gate.TryEnter());

        var busy = await ReadFramesAsync(admin, project);
        gate.Leave();
        var free = await ReadFramesAsync(admin, project);

        Assert.Null(busy[^1].Function);
        Assert.Equal("apply_discount", free[^1].Function);
    }

    [Fact]
    public async Task A_read_past_its_budget_answers_with_the_frames_as_stored()
    {
        // No client timeout, so only the read's own budget can end the wait.
        var (app, _) = App(new HangingHandler(), clientTimeout: Timeout.InfiniteTimeSpan);
        var (admin, project) = await ProvisionAsync(app);
        var cart = Golden("cart.so");
        await UploadAsync(app, admin, project, cart);
        await SeedAsync(project, NativeEvent([(ElfDebugFile.ToBuild(cart).Build!.DebugId, 0x1000_0000UL)], [0x1000_4600UL]));

        var read = ReadFramesAsync(admin, project);
        var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(60)));

        Assert.Same(read, finished);
        Assert.Null(Assert.Single(await read).Function);
    }

    [Fact]
    public async Task A_caller_that_gives_up_is_told_so_rather_than_answered()
    {
        var (app, _) = App();
        var (admin, project) = await ProvisionAsync(app);
        var cart = Golden("cart.so");
        await UploadAsync(app, admin, project, cart);
        var crash = NativeEvent([(ElfDebugFile.ToBuild(cart).Build!.DebugId, 0x1000_0000UL)], [0x1000_4600UL]);
        var stored = new StoredEvent(crash.EventId, "", "error", "", "", "", JsonSerializer.Serialize(crash));
        var symbolication = app.Services.GetRequiredService<Condux.Storage.SourceMaps.EventSymbolication>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => symbolication.SymbolicateAsync(project, [stored], new CancellationToken(canceled: true)));
    }

    [Theory]
    [InlineData("D")]
    [InlineData("N")]
    [InlineData("B")]
    [InlineData("aged")]
    public async Task An_image_whatever_its_debug_id_spelling_still_finds_its_file(string spelling)
    {
        var (app, _) = App();
        var (admin, project) = await ProvisionAsync(app);
        var cart = Golden("cart.so");
        await UploadAsync(app, admin, project, cart);
        var id = Guid.Parse(ElfDebugFile.ToBuild(cart).Build!.DebugId);
        var debugId = (spelling == "aged" ? id.ToString() + "-0" : id.ToString(spelling)).ToUpperInvariant();
        await SeedAsync(project, NativeEvent([(debugId, 0x1000_0000UL)], [0x1000_4600UL]));

        var frames = await ReadFramesAsync(admin, project);

        Assert.Equal("apply_discount", frames[^1].Function);
    }

    [Fact]
    public async Task A_second_read_of_an_issue_asks_the_symbolizer_nothing()
    {
        var recording = new RecordingHandler(_symbolizer.Server.CreateHandler());
        var (app, _) = App(recording);
        var (admin, project) = await ProvisionAsync(app);
        await UploadAsync(app, admin, project, Golden("flutter-crashapp.symbols"));
        await SeedAsync(project, FlutterCrash());

        var first = await ReadFramesAsync(admin, project);
        var calls = recording.Requests;
        var second = await ReadFramesAsync(admin, project);

        Assert.Equal(calls, recording.Requests);
        Assert.Single(recording.Resolved);
        Assert.Equal(first.Select(f => (f.Function, f.Filename, f.Lineno)), second.Select(f => (f.Function, f.Filename, f.Lineno)));
        Assert.Equal("CartService.applyDiscount", second[^1].Function);
    }

    [Fact]
    public async Task A_file_already_handed_over_is_not_sent_again_and_only_new_offsets_are_asked()
    {
        var recording = new RecordingHandler(_symbolizer.Server.CreateHandler());
        var (app, _) = App(recording);
        var (admin, project) = await ProvisionAsync(app);
        var cart = Golden("cart.so");
        await UploadAsync(app, admin, project, cart);
        var image = (ElfDebugFile.ToBuild(cart).Build!.DebugId, 0x1000_0000UL);
        var symbolication = app.Services.GetRequiredService<Condux.Storage.SourceMaps.EventSymbolication>();

        await symbolication.SymbolicateAsync(project, NativeEvent([image], [0x1000_4600UL]));
        var later = await symbolication.SymbolicateAsync(project, NativeEvent([image], [0x1000_4600UL, 0x1000_4604UL]));

        Assert.Equal(1, recording.Puts);
        Assert.Equal([["0x4600"], ["0x4604"]], recording.Resolved.Select(r => r.Addresses));
        // The offset answered from what the first call kept still reads as its function.
        Assert.Contains(later.Exceptions[^1].Stacktrace!.Frames, f => f.Function == "apply_discount");
    }

    [Fact]
    public async Task An_upload_records_the_hash_of_its_bytes_over_one_a_read_wrote_meanwhile()
    {
        var (app, store) = App();
        var (admin, project) = await ProvisionAsync(app);
        var cart = Golden("cart.so");
        // A read that found the row without a hash and recorded one for the bytes the upload is replacing.
        store.DuringPut = () => ExecuteAsync(
            $"UPDATE debug_files SET sha256 = '{new string('0', 64)}' WHERE project_id = @p AND sha256 IS NULL", project);

        await UploadAsync(app, admin, project, cart);

        var found = await new DebugFileRepository(_pg.ConnectionString)
            .FindAsync(project, [ElfDebugFile.ToBuild(cart).Build!.DebugId]);
        Assert.Equal(DebugFileHashes.ToSha256(cart), Assert.Single(found).Value.Sha256);
    }

    [Fact]
    public async Task A_read_corrects_a_row_whose_hash_names_bytes_the_bucket_no_longer_holds()
    {
        var recording = new RecordingHandler(_symbolizer.Server.CreateHandler());
        var (app, _) = App(recording);
        var (admin, project) = await ProvisionAsync(app);
        var cart = Golden("cart.so");
        await UploadAsync(app, admin, project, cart);
        // As two uploads of one build leave it when their hash writes finish in the other order.
        await ExecuteAsync($"UPDATE debug_files SET sha256 = '{new string('0', 64)}' WHERE project_id = @p", project);
        var crash = NativeEvent([(ElfDebugFile.ToBuild(cart).Build!.DebugId, 0x1000_0000UL)], [0x1000_4600UL]);
        var symbolication = app.Services.GetRequiredService<Condux.Storage.SourceMaps.EventSymbolication>();

        await symbolication.SymbolicateAsync(project, crash);
        var calls = recording.Requests;
        var again = await symbolication.SymbolicateAsync(project, crash);

        var found = await new DebugFileRepository(_pg.ConnectionString)
            .FindAsync(project, [ElfDebugFile.ToBuild(cart).Build!.DebugId]);
        Assert.Equal(DebugFileHashes.ToSha256(cart), Assert.Single(found).Value.Sha256);
        Assert.Equal(calls, recording.Requests);
        Assert.Equal(1, recording.Puts);
        Assert.Contains(again.Exceptions[^1].Stacktrace!.Frames, f => f.Function == "apply_discount");
    }

    [Fact]
    public async Task A_read_does_not_undo_a_hash_recorded_after_it_looked()
    {
        var (app, _) = App();
        var (admin, project) = await ProvisionAsync(app);
        var cart = Golden("cart.so");
        await UploadAsync(app, admin, project, cart);
        var repository = new DebugFileRepository(_pg.ConnectionString);
        var debugId = ElfDebugFile.ToBuild(cart).Build!.DebugId;
        var row = Assert.Single(await repository.FindAsync(project, [debugId])).Value;
        var stale = new string('1', 64);

        // A read that saw no hash, and one that saw a hash since replaced, both lose to the row as it is now.
        await repository.CorrectSha256Async(row.Id, seen: null, stale);
        await repository.CorrectSha256Async(row.Id, seen: new string('2', 64), stale);
        var kept = Assert.Single(await repository.FindAsync(project, [debugId])).Value.Sha256;
        await repository.CorrectSha256Async(row.Id, seen: row.Sha256, stale);
        var corrected = Assert.Single(await repository.FindAsync(project, [debugId])).Value.Sha256;

        Assert.Equal(DebugFileHashes.ToSha256(cart), kept);
        Assert.Equal(stale, corrected);
    }

    [Fact]
    public async Task An_answer_in_another_order_than_asked_is_neither_used_nor_kept()
    {
        var cart = Golden("cart.so");
        var recording = new RecordingHandler(
            _symbolizer.Server.CreateHandler(), failResolveOf: DebugFileHashes.ToSha256(cart), failure: Failure.Reordered);
        var (app, _) = App(recording);
        var (admin, project) = await ProvisionAsync(app);
        await UploadAsync(app, admin, project, cart);
        var crash = NativeEvent([(ElfDebugFile.ToBuild(cart).Build!.DebugId, 0x1000_0000UL)], [0x1000_4600UL, 0x1000_4604UL]);
        var symbolication = app.Services.GetRequiredService<Condux.Storage.SourceMaps.EventSymbolication>();

        var first = await symbolication.SymbolicateAsync(project, crash);
        await symbolication.SymbolicateAsync(project, crash);

        Assert.All(first.Exceptions[^1].Stacktrace!.Frames, f => Assert.Null(f.Function));
        Assert.Equal(2, recording.Resolved.Count);
    }

    [Fact]
    public async Task A_page_of_events_asks_for_each_offset_once()
    {
        var recording = new RecordingHandler(_symbolizer.Server.CreateHandler());
        var (app, _) = App(recording);
        var (admin, project) = await ProvisionAsync(app);
        var cart = Golden("cart.so");
        await UploadAsync(app, admin, project, cart);
        var crash = NativeEvent([(ElfDebugFile.ToBuild(cart).Build!.DebugId, 0x1000_0000UL)], [0x1000_4600UL]);
        await SeedAsync(project, crash);
        await SeedAsync(project, crash with { EventId = Guid.NewGuid().ToString("N") });

        var page = await ReadPageFramesAsync(admin, project, limit: 2);

        Assert.Equal(["apply_discount", "apply_discount"], page.Select(frames => frames[^1].Function));
        Assert.Single(recording.Resolved);
    }

    [Fact]
    public async Task A_file_the_symbolizer_refuses_is_not_offered_again_until_its_refusal_is_forgotten()
    {
        var cart = Golden("cart.so");
        var recording = new RecordingHandler(_symbolizer.Server.CreateHandler(), refusePutOf: DebugFileHashes.ToSha256(cart));
        var time = new MovableTime();
        var (app, _) = App(recording, time: time);
        var (admin, project) = await ProvisionAsync(app);
        await UploadAsync(app, admin, project, cart);
        var crash = NativeEvent([(ElfDebugFile.ToBuild(cart).Build!.DebugId, 0x1000_0000UL)], [0x1000_4600UL]);
        var symbolication = app.Services.GetRequiredService<Condux.Storage.SourceMaps.EventSymbolication>();

        var refused = await symbolication.SymbolicateAsync(project, crash);
        var calls = recording.Requests;
        await symbolication.SymbolicateAsync(project, crash);
        var asked = recording.Requests;
        time.Advance(SymbolizerCaches.RefusalMemory + TimeSpan.FromSeconds(1));
        await symbolication.SymbolicateAsync(project, crash);

        Assert.Null(refused.Exceptions[^1].Stacktrace!.Frames[0].Function);
        Assert.Equal(calls, asked);
        Assert.Equal(2, recording.Puts);
        Assert.Empty(recording.Resolved);
    }

    [Fact]
    public async Task Every_answer_from_the_symbolizer_is_counted_by_call_and_status()
    {
        var cart = Golden("cart.so");
        var recording = new RecordingHandler(
            _symbolizer.Server.CreateHandler(), failResolveOf: DebugFileHashes.ToSha256(cart), failure: Failure.Busy);
        var (app, _) = App(recording);
        var (admin, project) = await ProvisionAsync(app);
        await UploadAsync(app, admin, project, cart);
        var counted = new System.Collections.Concurrent.ConcurrentQueue<(string Call, int Status)>();
        var meters = app.Services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();
        using var listener = new System.Diagnostics.Metrics.MeterListener
        {
            // Only this app's meter, so a test running beside it cannot add to the count.
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Scope == meters && instrument.Name == "condux.symbolizer.responses")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var call = (string)tags.ToArray().Single(t => t.Key == "call").Value!;
            var status = (int)tags.ToArray().Single(t => t.Key == "http.response.status_code").Value!;
            for (var i = 0; i < value; i++)
            {
                counted.Enqueue((call, status));
            }
        });
        listener.Start();
        var symbolication = app.Services.GetRequiredService<Condux.Storage.SourceMaps.EventSymbolication>();

        await symbolication.SymbolicateAsync(project, NativeEvent([(ElfDebugFile.ToBuild(cart).Build!.DebugId, 0x1000_0000UL)], [0x1000_4600UL]));

        Assert.Equal([("holds", 404), ("put", 204), ("resolve", 429)], counted);
    }

    [Fact]
    public async Task A_symbolizer_that_is_down_is_counted_as_a_native_failure()
    {
        // A refused connection gets no answer, so the response counter cannot see it. This counter is the
        // one that says the symbolizer is down.
        var (app, _) = App(new FailingHandler());
        var (admin, project) = await ProvisionAsync(app);
        var cart = Golden("cart.so");
        await UploadAsync(app, admin, project, cart);
        var passes = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var meters = app.Services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();
        using var listener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Scope == meters && instrument.Name == "condux.symbolication.failures")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            passes.Enqueue((string)tags.ToArray().Single(t => t.Key == "pass").Value!));
        listener.Start();
        var symbolication = app.Services.GetRequiredService<Condux.Storage.SourceMaps.EventSymbolication>();

        await symbolication.SymbolicateAsync(project, NativeEvent([(ElfDebugFile.ToBuild(cart).Build!.DebugId, 0x1000_0000UL)], [0x1000_4600UL]));

        Assert.Equal(["native"], passes);
    }

    [Fact]
    public async Task A_budget_the_source_map_pass_ran_out_is_counted_once_and_not_against_the_native_pass()
    {
        // The passes after it would only fail on the spent budget too, which would count one wait several
        // times and blame passes that never got to run. The crash carries a debug image and exception types,
        // so the class-name pass would look them up if it ran.
        var (app, _) = App(new FailingHandler());
        var (_, project) = await ProvisionAsync(app);
        await new SourceMapArtifactRepository(_pg.ConnectionString).RecordAsync(
            project, "app@1.4.2", dist: null, debugId: "slow", filename: "app.min.js",
            objectKey: "sourcemaps/by-debug-id/slow", byteSize: 1);
        var native = NativeEvent([("ab000000000000000000000000000000", 0x1000_0000UL)], [0x1000_4600UL]);
        var crash = native with
        {
            Release = "app@1.4.2",
            DebugImages =
            [
                .. native.DebugImages,
                new DebugImage { Type = "sourcemap", CodeFile = "https://app/js/app.min.js", DebugId = "slow" },
            ],
            Exceptions =
            [
                new ExceptionValue
                {
                    Type = "TypeError",
                    Stacktrace = new Stacktrace
                    {
                        Frames =
                        [
                            new Frame
                            {
                                InApp = true, Filename = "app.min.js", AbsPath = "https://app/js/app.min.js",
                                Function = "n", Lineno = 2, Colno = 6,
                            },
                        ],
                    },
                },
                .. native.Exceptions,
            ],
        };
        using var recorder = new TestMetrics.SymbolicationRecorder();
        var symbolication = new Condux.Storage.SourceMaps.EventSymbolication(
            new Condux.Storage.SourceMaps.FrameSymbolicator(
                new SourceMapArtifactRepository(_pg.ConnectionString), new HangingMapStore()),
            app.Services.GetRequiredService<NativeSymbolicator>(),
            app.Services.GetRequiredService<Condux.Storage.DebugFiles.DartTypeNames>(), recorder.Metrics,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Condux.Storage.SourceMaps.EventSymbolication>.Instance);

        await symbolication.SymbolicateAsync(project, crash).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(["source-map"], recorder.Measurements
            .Where(m => m.Instrument == "condux.symbolication.failures").Select(m => m.Pass));
    }

    [Theory]
    [InlineData(Failure.Unavailable)]
    [InlineData(Failure.Malformed)]
    [InlineData(Failure.Oversized)]
    [InlineData(Failure.Busy)]
    public async Task An_image_the_symbolizer_fails_on_costs_its_own_frames_and_not_the_others(Failure failure)
    {
        var cart = Golden("cart.so");
        var broken = SymbolsFile("ab000000000000000000000000000000");
        var recording = new RecordingHandler(
            _symbolizer.Server.CreateHandler(), failResolveOf: DebugFileHashes.ToSha256(broken), failure: failure);
        var (app, _) = App(recording);
        var (admin, project) = await ProvisionAsync(app);
        await UploadAsync(app, admin, project, cart);
        await UploadAsync(app, admin, project, broken);
        // The broken image's frame is nearest the crash, so it is tried first.
        await SeedAsync(project, NativeEvent(
            [(ElfDebugFile.ToBuild(cart).Build!.DebugId, 0x1000_0000UL), (ElfDebugFile.ToBuild(broken).Build!.DebugId, 0x2000_0000UL)],
            [0x1000_4600UL, 0x2000_0010UL]));

        var frames = await ReadFramesAsync(admin, project);

        Assert.Equal(["cart_total", "apply_discount", null], frames.Select(f => f.Function));
    }

    [Fact]
    public void A_symbolizer_url_that_is_not_a_url_stops_the_service_at_startup()
    {
        using var app = ControlPlaneApp.Create(_pg.ConnectionString, _ch, configure: b =>
        {
            b.UseSetting("CONDUX_S3_ENDPOINT", "http://s3.invalid:9000");
            b.UseSetting("CONDUX_S3_BUCKET", "test-bucket");
            b.UseSetting("CONDUX_S3_ACCESS_KEY", "test");
            b.UseSetting("CONDUX_S3_SECRET_KEY", "test");
            b.UseSetting("CONDUX_SYMBOLIZER_URL", "symbolizer:9100");
        });

        var refused = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        Assert.Contains("CONDUX_SYMBOLIZER_URL", refused.Message);
    }

    private (WebApplicationFactory<Program> App, RecordingObjectStore Store) App(
        HttpMessageHandler? symbolizer = null, TimeSpan? clientTimeout = null, TimeProvider? time = null)
    {
        var store = new RecordingObjectStore();
        var app = ControlPlaneApp.Create(_pg.ConnectionString, _ch, configure: b =>
        {
            b.UseSetting("CONDUX_S3_ENDPOINT", "http://s3.invalid:9000");
            b.UseSetting("CONDUX_S3_BUCKET", "test-bucket");
            b.UseSetting("CONDUX_S3_ACCESS_KEY", "test");
            b.UseSetting("CONDUX_S3_SECRET_KEY", "test");
            b.UseSetting("CONDUX_SYMBOLIZER_URL", "http://symbolizer/");
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<IObjectStore>(store);
                if (time is not null)
                {
                    s.AddSingleton(new SymbolizerCaches(time));
                }
                var client = s.AddHttpClient(SymbolizerClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => symbolizer ?? _symbolizer.Server.CreateHandler());
                if (clientTimeout is { } timeout)
                {
                    client.ConfigureHttpClient(c => c.Timeout = timeout);
                }
            });
        });
        return (app, store);
    }

    private async Task<(HttpClient Admin, long Project)> ProvisionAsync(WebApplicationFactory<Program> app) =>
        await ReleaseTokenUploads.ProvisionAsync(app, _pg.ConnectionString);

    private static async Task UploadAsync(WebApplicationFactory<Program> app, HttpClient admin, long project, byte[] file)
    {
        var ci = ReleaseTokenUploads.Bearer(app, await ReleaseTokenUploads.MintTokenAsync(admin, project));
        var response = await ci.PostAsync("/api/debug-files", new ByteArrayContent(file));
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
    }

    // The event as the consumer would store it: grouped into an issue, then written to ClickHouse.
    private async Task SeedAsync(long project, Event crash)
    {
        var grouping = Fingerprinter.Compute(crash);
        var issue = await new IssueRepository(_pg.ConnectionString)
            .UpsertAsync(project, grouping, Level.Error, DateTimeOffset.UtcNow);
        using var http = new HttpClient();
        ClickHouseRegistration.Configure(http, _ch.BaseUrl, _ch.ChUser, _ch.ChPassword);
        var stored = crash with { TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        await new ClickHouseEventWriter(http).InsertAsync([ClickHouseEventWriter.ToRow(
            project.ToString(), (ulong)issue.Id, stored, grouping.Fingerprint, PlanCatalog.For(Tier.Free).RetentionDays,
            DateTimeOffset.UtcNow)]);
    }

    // The frames of the last exception in the issue's latest event, as the issue page receives them.
    private static async Task<IReadOnlyList<Frame>> ReadFramesAsync(HttpClient admin, long project)
    {
        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/issues");
        var id = list.GetProperty("issues")[0].GetProperty("id").GetString();
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/issues/{id}");
        var payload = detail.GetProperty("events")[0].GetProperty("payload").GetString()!;
        return JsonSerializer.Deserialize<Event>(payload)!.Exceptions[^1].Stacktrace!.Frames;
    }

    // The last exception's frames of each event on the issue's events page.
    private static async Task<IReadOnlyList<IReadOnlyList<Frame>>> ReadPageFramesAsync(HttpClient admin, long project, int limit)
    {
        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/issues");
        var id = list.GetProperty("issues")[0].GetProperty("id").GetString();
        var page = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/issues/{id}/events?limit={limit}&offset=0");
        return [.. page.GetProperty("events").EnumerateArray().Select(e =>
            (IReadOnlyList<Frame>)JsonSerializer.Deserialize<Event>(e.GetProperty("payload").GetString()!)!.Exceptions[^1].Stacktrace!.Frames)];
    }

    private static Event NativeEvent(IReadOnlyList<(string DebugId, ulong Base)> images, IReadOnlyList<ulong> addresses) =>
        new()
        {
            EventId = Guid.NewGuid().ToString("N"),
            Level = Level.Error,
            DebugImages = [.. images.Select(i => new DebugImage
            {
                Type = "elf", DebugId = i.DebugId, ImageAddr = $"0x{i.Base:x}", ImageSize = 0x100000,
            })],
            Exceptions =
            [
                new ExceptionValue
                {
                    Type = "SIGSEGV",
                    Stacktrace = new Stacktrace
                    {
                        Frames = [.. addresses.Select(a => new Frame { InstructionAddr = $"0x{a:x}" })],
                    },
                },
            ],
        };

    private async Task ExecuteAsync(string sql, long project)
    {
        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("p", project);
        await cmd.ExecuteNonQueryAsync();
    }

    private static ResolveRequest ToResolveRequest(byte[] body) =>
        JsonSerializer.Deserialize<ResolveRequest>(body, JsonSerializerOptions.Web)!;

    // A symbolizer that never answers, until the caller gives up.
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    // A symbolizer that cannot be reached.
    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("the symbolizer is down");
    }

    // The real symbolizer for files, with every resolve answered by a chain of the given depth, innermost
    // ("f0") first, as a debug file with deep inlining would make it answer.
    private sealed class DeepChainHandler(HttpMessageHandler inner, int depth) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/resolve", StringComparison.Ordinal))
            {
                return await base.SendAsync(request, ct);
            }
            var asked = ToResolveRequest(await request.Content!.ReadAsByteArrayAsync(ct));
            var chain = Enumerable.Range(0, depth).Select(i => new ResolvedFrame($"f{i}" + new string('x', 4000), "deep.c", i + 1, 0)).ToList();
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ResolveResponse([.. asked.Addresses.Select(a => new ResolvedAddress(a, chain))])),
            };
        }
    }

    // The real symbolizer, with what was handed over and asked recorded, and optionally a file it fails on.
    public enum Failure
    {
        Unavailable,
        Malformed,
        Oversized,
        Busy,
        Reordered,
    }

    private sealed class RecordingHandler(
        HttpMessageHandler inner, string? failResolveOf = null, Failure failure = Failure.Unavailable,
        string? refusePutOf = null)
        : DelegatingHandler(inner)
    {
        private int _puts;
        private int _requests;

        public int Puts => _puts;

        // Every call to the symbolizer, of any kind.
        public int Requests => _requests;

        public System.Collections.Concurrent.ConcurrentQueue<ResolveRequest> Resolved { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _requests);
            if (request.Method == HttpMethod.Put)
            {
                Interlocked.Increment(ref _puts);
                // As the symbolizer refuses a file it cannot use.
                if (refusePutOf is not null && request.RequestUri!.AbsolutePath.EndsWith(refusePutOf, StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest);
                }
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/resolve", StringComparison.Ordinal))
            {
                // Read as bytes, which leaves the body for the handler behind this one; a JSON read would
                // close the one stream the content hands out.
                var asked = ToResolveRequest(await request.Content!.ReadAsByteArrayAsync(ct));
                Resolved.Enqueue(asked);
                if (asked.File == failResolveOf)
                {
                    return failure switch
                    {
                        Failure.Malformed => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                        {
                            Content = JsonContent.Create(new { results = (object?)null }),
                        },
                        // Well formed and past the cap: one address answered with a 9 MiB name.
                        Failure.Oversized => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                        {
                            Content = JsonContent.Create(new ResolveResponse([.. asked.Addresses.Select(a => new ResolvedAddress(
                                a, [new ResolvedFrame(new string('x', 9 * 1024 * 1024), null, 1, 0)]))])),
                        },
                        Failure.Busy => new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests),
                        // Well formed in every other way: a frame per address, in the reverse order.
                        Failure.Reordered => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                        {
                            Content = JsonContent.Create(new ResolveResponse([.. asked.Addresses.Reverse().Select(a => new ResolvedAddress(
                                a, [new ResolvedFrame("swapped", "swapped.c", 1, 0)]))])),
                        },
                        _ => new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway),
                    };
                }
            }
            return await base.SendAsync(request, ct);
        }
    }
}
