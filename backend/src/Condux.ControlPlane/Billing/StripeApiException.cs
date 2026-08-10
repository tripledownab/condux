namespace Condux.ControlPlane.Billing;

/// <summary>
/// Thrown then immediately caught so a Stripe API failure self-reports to Condux's own error monitoring
/// (#75 dogfooding). Throwing is what populates the stack trace the .NET SDK reads. The message is
/// deliberately stable (the operation + HTTP status, no per-request body) so repeated failures group into
/// one Condux issue; the full Stripe error body rides the Error-level log line instead. The billing
/// endpoints still return their own typed error to the caller — this never propagates out of the client.
/// </summary>
internal sealed class StripeApiException(string operation, int statusCode)
    : Exception($"Stripe {operation} failed (HTTP {statusCode})");
