namespace Acme.Checkout;

// Stands in for customer code, and the namespace is the whole point.
//
// Every other type in this test project lives under Condux.Sdk.Tests, which the SDK now correctly treats
// as its own plumbing rather than the application. A test that threw from there and asserted the frame
// was in-app would only have been passing because the test happens to sit inside our namespace, which is
// an accident and not a property of the code under test.
internal static class OrderService
{
    // Throw + catch so the exception carries a real stack trace.
    internal static Exception Thrown()
    {
        try
        {
            throw new InvalidOperationException("boom from the sdk");
        }
        catch (Exception error)
        {
            return error;
        }
    }
}
