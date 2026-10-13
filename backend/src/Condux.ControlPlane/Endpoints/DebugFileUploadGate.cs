using Condux.Core.Http;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// How many debug file uploads one control-plane holds in memory at once. Each holds its whole body while it
/// is checked and stored, and a release token comes from an org anyone can create, so without a count the
/// memory several concurrent uploads take is a number a stranger chooses.
/// </summary>
internal sealed class DebugFileUploadGate() : AdmissionGate(Places)
{
    // CI uploads one file per request, one after another, so a few places serve many pipelines.
    private const int Places = 2;
}
