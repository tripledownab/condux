using Condux.Core.SourceMaps;
using Condux.Storage.SourceMaps;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The cache a symbolication call shares across its events: a key is loaded once, and past the cap the
/// least recently used map goes, never one used since. Needs no store, so it carries no category.
/// </summary>
public sealed class SourceMapCacheTest
{
    [Fact]
    public async Task Loads_a_key_once_and_evicts_the_least_recently_used()
    {
        var cache = new SourceMapCache(capacity: 2);
        var loads = new List<string>();
        Task<SourceMap?> Get(string key) => cache.GetOrLoadAsync(
            key, _ => { loads.Add(key); return Task.FromResult<SourceMap?>(null); }, CancellationToken.None);

        await Get("a");
        await Get("b");
        await Get("a"); // a is now the most recent, so b is the one to go
        await Get("c");
        await Get("a");
        await Get("b");

        Assert.Equal(["a", "b", "c", "b"], loads);
    }

    // One event reaches at most 64 maps (32 lookup keys of each kind), and the default holds exactly that
    // many: all of one event's maps, so its frames never read one twice, and not one more, so a page holds
    // no more parsed maps than one event could.
    [Fact]
    public async Task By_default_holds_every_map_one_event_can_reach_and_no_more()
    {
        var cache = new SourceMapCache();
        var loads = new List<string>();
        Task Get(string key) => cache.GetOrLoadAsync(
            key, _ => { loads.Add(key); return Task.FromResult<SourceMap?>(null); }, CancellationToken.None);
        var keys = Enumerable.Range(0, 64).Select(i => $"map-{i}").ToList();

        foreach (var key in keys.Concat(keys))
        {
            await Get(key);
        }
        Assert.Equal(64, loads.Count);

        await Get("map-64"); // a 65th map evicts the least recently used, map-0
        await Get("map-0");
        Assert.Equal(["map-64", "map-0"], loads.Skip(64));
    }

    // The oldest entry is gone before the next map is parsed, so the map being parsed is never one over the
    // cap. Asking for it from inside the load shows which came first.
    [Fact]
    public async Task Evicts_before_it_loads()
    {
        var cache = new SourceMapCache(capacity: 1);
        var loads = new List<string>();
        Task<SourceMap?> Load(string key)
        {
            loads.Add(key);
            return Task.FromResult<SourceMap?>(null);
        }
        await cache.GetOrLoadAsync("a", _ => Load("a"), CancellationToken.None);

        await cache.GetOrLoadAsync("b", async ct =>
        {
            await cache.GetOrLoadAsync("a", _ => Load("a again"), ct);
            return await Load("b");
        }, CancellationToken.None);

        Assert.Equal(["a", "a again", "b"], loads);
    }
}
