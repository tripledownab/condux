namespace Condux.Sdk;

/// <summary>Event severity, matching the levels the Condux relay understands. The wire value is the
/// lowercase name (e.g. <see cref="Error"/> → <c>"error"</c>).</summary>
public enum Level
{
    Debug,
    Info,
    Warning,
    Error,
    Fatal,
}
