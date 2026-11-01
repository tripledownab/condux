using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Primitives;

namespace Condux.Relay.Ingest;

/// <summary>
/// The address a body's sender is charged as in the relay's body budget (<see cref="IngestBodyBudget"/>),
/// within its organisation's ceiling. A DSN is public, so the address is what keeps a stranger holding one
/// from taking the room that organisation's own users read into.
/// </summary>
internal static class IngestSenders
{
    public static string ToAddress(HttpContext ctx, IReadOnlyList<IPNetwork> trustedProxies) =>
        ToAddress(ctx.Connection.RemoteIpAddress, ctx.Request.Headers["X-Forwarded-For"], trustedProxies);

    /// <summary>
    /// The client's address: the peer, unless the peer is one of <paramref name="trustedProxies"/>, in
    /// which case the last X-Forwarded-For entry, the one that proxy wrote; any earlier entry is what the
    /// client claimed. Caddy, in front of the relay in production, replaces the header for a client it does
    /// not trust and appends to it otherwise, so its last entry is the peer it saw. IPv6 is grouped by
    /// /64, the block one host is usually given.
    /// </summary>
    public static string ToAddress(IPAddress? peer, StringValues forwardedFor, IReadOnlyList<IPNetwork> trustedProxies)
    {
        var address = peer is null ? null : Unmapped(peer);
        if (address is not null && trustedProxies.Any(network => network.Contains(address))
            && ToLastForwarded(forwardedFor) is { } client)
        {
            address = Unmapped(client);
        }
        return address is null ? "-" : ToGroup(address);
    }

    private static IPAddress? ToLastForwarded(StringValues forwardedFor)
    {
        var last = forwardedFor.Count == 0 ? null : forwardedFor[^1];
        var entry = last?[(last.LastIndexOf(',') + 1)..].Trim();
        return IPAddress.TryParse(entry, out var address) ? address : null;
    }

    private static IPAddress Unmapped(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static string ToGroup(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }
}
