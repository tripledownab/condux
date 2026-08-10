using Condux.Telemetry;
using Xunit;

namespace Condux.Conductor.Tests;

public sealed class ConduxSelfReporterTests
{
    [Fact]
    public void Report_WhenSelfReportingIsOff_IsASilentNoOp()
    {
        // No client (CONDUX_SELF_DSN unset) — Report must be a no-op that never throws, so a worker's
        // error-handling path stays safe whether or not self-reporting is configured.
        var reporter = new ConduxSelfReporter(null);

        var thrown = Record.Exception(() => reporter.Report(new InvalidOperationException("worker boom")));

        Assert.Null(thrown);
    }
}
