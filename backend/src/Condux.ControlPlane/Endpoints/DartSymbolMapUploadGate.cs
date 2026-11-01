using Condux.Core.Http;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// How many obfuscation maps one control-plane parses and writes to Postgres at once. A map is written name
/// by name, which takes seconds for a large one, and parsing one holds a string per name beside its body.
/// The body itself is already read and counted against the shared upload budget by the time a place is
/// taken, so a place is held only for that work, never for the time a sender takes to send.
/// </summary>
internal sealed class DartSymbolMapUploadGate() : AdmissionGate(Places)
{
    // An upload turned away answers 429, which the CI step's curl retries.
    private const int Places = 2;
}
