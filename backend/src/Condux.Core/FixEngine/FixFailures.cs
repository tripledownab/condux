using System.Net;

namespace Condux.Core.FixEngine;

// Why a fix run failed, as a closed set the dashboard can explain and suggest a next step for. The raw
// error text stays in the audit trail for operators and is never shown, because several writers put
// free text there, a provider's error body among them. The numbers are stored, so never reuse one. The
// dashboard keeps its own copy with an explanation per reason, and web/src/issues/fix-format.test.ts reads
// this file and fails when the two differ.
public enum FixFailureReason
{
    /// <summary>Not classified. Shown as exactly that, never as a guess.</summary>
    Unknown = 0,

    /// <summary>No stack frame resolved to a file in the repository: a code mapping is missing or wrong,
    /// or the frames point at bundled output with no source map.</summary>
    NoSourceFiles = 1,

    /// <summary>GitHub is not connected, or the App cannot reach this repository.</summary>
    SourceHostAccess = 2,

    /// <summary>The model provider refused the API key.</summary>
    ModelKeyRejected = 3,

    /// <summary>The model provider was rate limiting or failing at the time.</summary>
    ModelUnavailable = 4,

    /// <summary>The model answered, but not with a fix that could be applied.</summary>
    NoUsableFix = 5,

    /// <summary>A model or GitHub call, or the run as a whole, took too long.</summary>
    TimedOut = 6,

    /// <summary>The run was still in progress long after it should have finished, most often because
    /// the Conductor restarted during it, so nothing was left to finish it.</summary>
    Interrupted = 7,

    /// <summary>The model declined to produce a fix, for example because a safety check refused its
    /// response.</summary>
    ModelRefused = 8,

    /// <summary>The agent stopped to wait for a confirmation or a tool result that Condux does not
    /// supply, so the run could not go on.</summary>
    AgentBlocked = 9,

    /// <summary>The run was stopped from outside Condux, for example from the vendor's console, before it
    /// finished.</summary>
    StoppedExternally = 10,

    /// <summary>The files the error points to exist, but none could be read as text: larger than the
    /// source host returns, or not UTF-8. Such a file is never shown to the model, since it could not be
    /// changed without seeing all of it.</summary>
    SourceFilesUnreadable = 11,
}

/// <summary>A failure whose reason is known where it happens. Throw this rather than a bare exception
/// when the site can say why, since a message cannot be classified reliably afterwards. An
/// InvalidOperationException because every site it replaced threw one.</summary>
public class FixFailedException(FixFailureReason reason, string message, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    public FixFailureReason Reason { get; } = reason;
}

/// <summary>Turns an exception from a fix run into its <see cref="FixFailureReason"/>.</summary>
public static class FixFailures
{
    public static FixFailureReason Classify(Exception exception) => exception switch
    {
        FixFailedException failed => failed.Reason,
        TimeoutException or OperationCanceledException => FixFailureReason.TimedOut,
        // Inside a run only the source host client raises an HttpRequestException with a status, so an
        // authorization or not-found answer is GitHub refusing this installation or repository.
        HttpRequestException
        {
            StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
        } => FixFailureReason.SourceHostAccess,
        _ => FixFailureReason.Unknown,
    };

    /// <summary>The reason a model provider's error status means. Anything else it could mean is left
    /// Unknown rather than guessed.</summary>
    public static FixFailureReason ToModelReason(HttpStatusCode status) => (int)status switch
    {
        401 or 403 => FixFailureReason.ModelKeyRejected,
        429 or >= 500 => FixFailureReason.ModelUnavailable,
        _ => FixFailureReason.Unknown,
    };
}
