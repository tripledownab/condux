using Condux.Core.Http;

namespace Condux.Storage.DebugFiles;

/// <summary>How many debug files the native pass reads whole from storage at once, per process. A file can
/// be as large as the upload cap, and which files a read needs is chosen by whoever sent the event.</summary>
public sealed class DebugFileReadGate() : AdmissionGate(2);
