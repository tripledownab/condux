using StackExchange.Redis;

namespace Condux.Storage.Valkey;

/// <summary>
/// What counts as Valkey being unavailable, for the callers that fail open when it is. One home so a caller
/// cannot catch half of it: the client reports a timeout as RedisTimeoutException, which derives from
/// TimeoutException and not from RedisException, so a catch on RedisException alone lets a slow store fail
/// the request it was meant to wave through.
/// </summary>
public static class ValkeyOutages
{
    public static bool IsOutage(Exception ex) => ex is RedisException or RedisTimeoutException;
}
