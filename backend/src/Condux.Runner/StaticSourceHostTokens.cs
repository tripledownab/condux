using Condux.Core.SourceControl;

namespace Condux.Runner;

/// <summary>
/// The customer's own source-host credential, held by their process and never sent to us. The hosted
/// Conductor mints a short-lived installation token per run because it acts on many orgs' repos; a runner
/// acts only on its owner's, so a token they issue and rotate themselves is both simpler and the point:
/// self-hosting means the credential does not leave their boundary.
///
/// The installation id is ignored — there is no installation to look up, only the one token.
/// </summary>
internal sealed class StaticSourceHostTokens(string token) : ISourceHostTokens
{
    public Task<string> GetAsync(long installationId, CancellationToken ct = default) =>
        Task.FromResult(token);

    /// <summary>No installation exists here, and jobs always carry a zero id — that is the design, not a
    /// misconfiguration for the gateway to refuse.</summary>
    public bool RequiresInstallation => false;
}
