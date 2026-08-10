using Condux.Core.Events;
using Xunit;

namespace Condux.Core.Tests;

public class EventModelTests
{
    [Fact]
    public void Defaults_AreEmptyNotNull()
    {
        var e = new Event();
        Assert.Empty(e.Exceptions);
        Assert.Empty(e.Breadcrumbs);
        Assert.Empty(e.Tags);
        Assert.Empty(e.Extra);
        Assert.Empty(e.Fingerprint);
    }

    [Fact]
    public void Records_HaveValueEquality()
    {
        var a = new Frame { Filename = "app.py", Lineno = 10, InApp = true };
        var b = new Frame { Filename = "app.py", Lineno = 10, InApp = true };
        Assert.Equal(a, b);
    }
}
