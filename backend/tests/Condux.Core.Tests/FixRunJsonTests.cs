using System.Text.Json;
using Condux.Core.CveFix;
using Condux.Core.FixEngine;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>Both run records are served by the API as they are, so the allowance's bookkeeping on them must
/// not ride along into the contract.</summary>
public class FixRunJsonTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void A_fix_run_serializes_without_its_job_id_or_reservation()
    {
        var run = new FixSuggestion(
            Guid.CreateVersion7(), 1, "acme/api", FixStatus.Running, "fake", "m", "", "", "", Now, Now)
        { JobId = Guid.CreateVersion7(), ReservedAt = Now };

        AssertHidden(JsonSerializer.Serialize(run, Web));
    }

    [Fact]
    public void A_cve_bump_serializes_without_its_job_id_or_reservation()
    {
        var run = new CveFixRun(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "GHSA-x", null, "lodash", "npm", "< 1", "1",
            "https://example.invalid", FixStatus.Running, "fake", "m", "", "", "", "42", Now, Now)
        { JobId = Guid.CreateVersion7(), ReservedAt = Now };

        AssertHidden(JsonSerializer.Serialize(run, Web));
    }

    private static void AssertHidden(string json)
    {
        Assert.Contains("\"repo", json); // the run itself did serialize
        Assert.DoesNotContain("jobId", json);
        Assert.DoesNotContain("reservedAt", json);
    }
}
