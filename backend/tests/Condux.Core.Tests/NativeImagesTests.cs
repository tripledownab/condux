using Condux.Core.Events;
using Xunit;

namespace Condux.Core.Tests;

public class NativeImagesTests
{
    private static DebugImage Image(string debugId) =>
        new() { Type = "elf", DebugId = debugId, ImageAddr = "0x1000", ImageSize = 0x1000 };

    [Fact]
    public void Two_images_at_one_base_and_size_place_a_frame_in_the_same_one_whatever_their_order()
    {
        // The image chosen names the debug file the frame is resolved in, so it must not depend on the
        // order the sender listed them in.
        var frame = new Frame { InstructionAddr = "0x1010" };
        var a = Image("aaaaaaaa-0000-0000-0000-000000000000");
        var b = Image("bbbbbbbb-0000-0000-0000-000000000000");

        var first = NativeImages.From([a, b]).Place(frame);
        var second = NativeImages.From([b, a]).Place(frame);

        Assert.NotNull(first?.Image);
        Assert.Equal(first.Value.Image!.DebugId, second?.Image?.DebugId);
    }

    [Theory]
    [InlineData(null, null, null, true)]
    [InlineData("", "", "", true)]
    [InlineData(null, "app.c", null, false)]
    [InlineData("app", null, null, false)]
    [InlineData(null, null, "main", false)]
    [InlineData("", "app.c", null, true)]
    public void A_frame_is_address_only_when_grouping_would_key_it_on_its_offset(
        string? module, string? filename, string? function, bool addressOnly)
    {
        var frame = new Frame { Module = module, Filename = filename, Function = function };

        Assert.Equal(addressOnly, NativeImages.IsAddressOnly(frame));
    }
}
