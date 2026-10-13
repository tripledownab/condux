namespace Condux.Core.Http;

/// <summary>
/// A fixed number of places for work that holds memory a caller sizes, such as a request body held whole. A
/// request past the places is turned away at once rather than queued, so a slow sender cannot hold one place
/// and stack waiters behind it. Each use names its own subclass, so a service registers one per kind of work.
/// </summary>
public abstract class AdmissionGate(int places)
{
    private readonly SemaphoreSlim _places = new(places, places);

    public bool TryEnter() => _places.Wait(0);

    public void Leave() => _places.Release();
}
