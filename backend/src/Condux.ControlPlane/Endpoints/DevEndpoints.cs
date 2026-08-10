namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Dev-only dogfood levers (Condux on Condux, #75). <c>GET /api/boom</c> forces a genuine unhandled
/// exception so the self-report middleware captures the control-plane's own error into
/// <c>CONDUX_SELF_DSN</c> — the backend parity of the dashboard's <c>GET /api/boom</c>. Mapped only in
/// the Development environment and excluded from the OpenAPI doc: it is a debug lever, not part of the API.
/// </summary>
internal static class DevEndpoints
{
    public static void MapDevEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/boom", () => ChargeOrder(new Order("ord-dogfood-1")))
            .WithName("boom")
            .ExcludeFromDescription();
    }

    private sealed record Order(string Id, OrderTotal? Total = null);

    private sealed record OrderTotal(decimal Amount);

    // Deliberate: Total is absent, so this dereference throws the classic null-reference bug — reported as
    // the control-plane's own unhandled exception, with a realistic stack trace to group on.
    private static decimal ChargeOrder(Order order) => order.Total!.Amount;
}
