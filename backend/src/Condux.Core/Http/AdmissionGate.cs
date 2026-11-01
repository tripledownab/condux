namespace Condux.Core.Http;

/// <summary>
/// A fixed number of places for work that holds memory a caller sizes, or load the service would rather
/// bound by count. A request past the places is turned away at once rather than queued, so one slow holder
/// cannot stack waiters behind it. Each use names its own subclass, so a service registers one per kind of
/// work.
///
/// A place taken before a request body is read is held for as long as its sender takes to send it. Where a
/// stranger sizes and paces a body, read it under a <see cref="ByteBudget"/> with a minimum data rate set on
/// the request instead, as the control-plane's CI uploads do, and take a place, if one is still needed, once
/// the body is in.
/// </summary>
public abstract class AdmissionGate(int places)
{
    private readonly SemaphoreSlim _places = new(places, places);

    public bool TryEnter() => _places.Wait(0);

    public void Leave() => _places.Release();
}
