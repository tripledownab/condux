namespace Condux.Linear;

/// <summary>Whether a call to Linear failed, decided in one place so every caller draws the line between
/// "the call failed" and "we were cancelled" the same way.</summary>
public static class LinearHttp
{
    /// <summary>
    /// True for a call that produced no usable answer: Linear unreachable, the connection dropped, the
    /// client's own timeout (which .NET reports as a cancellation), or an error answer, which
    /// <see cref="LinearApi"/> reports as an <see cref="HttpRequestException"/>. False when
    /// <paramref name="ct"/> fired, because then the caller stopped asking and the exception belongs to it.
    /// </summary>
    public static bool IsCallFailure(Exception exception, CancellationToken ct) =>
        exception is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested;
}
