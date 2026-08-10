using Condux.Core.OrgNotifications;
using Xunit;

namespace Condux.Core.Tests;

public class ConductorPauseTextTests
{
    [Fact]
    public void CostCap_SubjectAndBody_NameTheReasonAndOrg()
    {
        Assert.Contains("compute limit", ConductorPauseText.Subject(ConductorPauseReason.CostCapReached));

        var body = ConductorPauseText.Body(ConductorPauseReason.CostCapReached, "Acme");
        Assert.Contains("Acme", body);
        Assert.Contains("AI-fix compute", body);
        Assert.Contains("request fixes manually", body); // the escape hatch is spelled out
    }

    [Fact]
    public void Allowance_SubjectAndBody_NameTheReasonAndOrg()
    {
        Assert.Contains("allowance", ConductorPauseText.Subject(ConductorPauseReason.AllowanceExhausted));

        var body = ConductorPauseText.Body(ConductorPauseReason.AllowanceExhausted, "Acme");
        Assert.Contains("Acme", body);
        Assert.Contains("AI-fix runs are used up", body);
    }

    [Fact]
    public void Copy_HasNoEmDash() // house style
    {
        foreach (var reason in new[] { ConductorPauseReason.CostCapReached, ConductorPauseReason.AllowanceExhausted })
        {
            Assert.DoesNotContain('—', ConductorPauseText.Subject(reason));
            Assert.DoesNotContain('—', ConductorPauseText.Body(reason, "Acme"));
        }
    }
}
