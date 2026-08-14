using Condux.Core.FixEngine;
using Condux.Core.SourceControl;

namespace Condux.Agent;

/// <summary>The start-time guard every gateway shares: only a token source that mints from an
/// installation needs one — a customer's runner brings its own credential and always sends
/// installation 0, which is by design, not an error.</summary>
internal static class SourceHostGuards
{
    public static void RequireInstallation(ISourceHostTokens tokens, AgentRunSpec spec)
    {
        if (spec.InstallationId == 0 && tokens.RequiresInstallation)
        {
            throw new InvalidOperationException(
                "The org has no GitHub App installation. Connect GitHub in settings before requesting a fix.");
        }
    }
}
