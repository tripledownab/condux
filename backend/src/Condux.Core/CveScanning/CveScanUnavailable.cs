namespace Condux.Core.CveScanning;

/// <summary>
/// Why a repository's findings could not be read at all. Each is something a person can act on, so the
/// surface names it rather than showing an empty list, which would read as "no vulnerabilities".
/// </summary>
public enum CveScanUnavailable
{
    /// <summary>No source host is connected for the org, so there was nothing to ask.</summary>
    NotConnected = 1,

    /// <summary>The source host has its alerts switched off for this repository.</summary>
    AlertsDisabled = 2,

    /// <summary>The connection lacks the permission to read alerts.</summary>
    PermissionMissing = 3,

    /// <summary>The read failed for any other reason; the log says which.</summary>
    Failed = 4,
}

/// <summary>Thrown by a scanner whose source host refused the read for a reason it can name.</summary>
public sealed class CveScanUnavailableException(CveScanUnavailable reason, string message)
    : Exception(message)
{
    public CveScanUnavailable Reason { get; } = reason;
}
