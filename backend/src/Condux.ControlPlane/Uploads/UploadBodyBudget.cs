using Condux.Core.Http;

namespace Condux.ControlPlane.Uploads;

/// <summary>
/// How much of the CI uploads' bodies one control-plane holds in memory at once: source maps, debug files and
/// obfuscation maps together, from every org. A release token comes from an org anyone can create, so
/// without a bound the memory concurrent uploads take is a number a stranger chooses. It is a budget of
/// bytes rather than a count of uploads because a count is taken before the body arrives, which let a sender
/// that trickled its body hold a place, and every other org's uploads with it, for as long as it liked.
/// </summary>
internal sealed class UploadBodyBudget() : ByteBudget(Bytes)
{
    // A body is charged about twice its size while it arrives (RequestBodies.ReadHeldAsync) and its size
    // after, so this is sized for several of the largest uploads arriving at once.
    public const long Bytes = 512L * 1024 * 1024;
}
