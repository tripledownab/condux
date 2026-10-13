using Condux.Conductor;
using Condux.Storage.Postgres;
using Condux.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Condux.Conductor.Tests;

/// <summary>
/// The verification watcher's loop runs the issue-fix half and the CVE bump half on one timer, and one
/// half failing must neither stop the loop nor skip the other half; only the host stopping ends it.
/// <see cref="VerificationWorker.TryTickAsync"/> is that decision, so it is what these drive. Nothing here
/// touches a store: the halves are stand-ins.
/// </summary>
public class VerificationWorkerTests
{
    [Fact]
    public async Task A_half_that_throws_lets_the_loop_go_on() =>
        Assert.True(await Worker().TryTickAsync(_ => throw new InvalidOperationException("db down"), "half", default));

    /// <summary>An HttpClient timeout arrives as a cancellation, and it is not the host stopping.</summary>
    [Fact]
    public async Task A_timeout_is_not_a_stop()
    {
        using var stopping = new CancellationTokenSource();

        Assert.True(await Worker().TryTickAsync(_ => throw new TaskCanceledException("timed out"), "half", stopping.Token));
    }

    [Fact]
    public async Task The_host_stopping_ends_the_loop()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        Assert.False(await Worker().TryTickAsync(ct => Task.FromCanceled(ct), "half", stopping.Token));
    }

    [Fact]
    public async Task A_half_that_completes_lets_the_loop_go_on() =>
        Assert.True(await Worker().TryTickAsync(_ => Task.CompletedTask, "half", default));

    // Every store is constructed but never opened: TryTickAsync runs only the half it is handed.
    private static VerificationWorker Worker()
    {
        const string NoDatabase = "Host=unused";
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new VerificationWorker(
            new ConfigurationBuilder().Build(),
            NullLogger<VerificationWorker>.Instance,
            scopes,
            new PostgresFixVerification(NoDatabase),
            new IssueRepository(NoDatabase),
            new PostgresFixStore(NoDatabase),
            new ProjectEventNotifier(NoDatabase),
            new ConduxSelfReporter(null),
            new CveFixVerifier(
                new PostgresCveFixVerification(NoDatabase), scopes, new ProjectEventNotifier(NoDatabase),
                TimeProvider.System, NullLogger<CveFixVerifier>.Instance));
    }
}
