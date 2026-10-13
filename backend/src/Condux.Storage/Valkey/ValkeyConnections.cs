using StackExchange.Redis;

namespace Condux.Storage.Valkey;

/// <summary>
/// Opens the connection the fail-open Valkey callers share. The client's default aborts when the server
/// cannot be reached at connect, so a relay starting while Valkey was down threw before it could serve a
/// request, and the limiter and the meter never had the chance to fail open. Here it starts anyway, and
/// reconnects in the background; until then each call fails as an outage (<see cref="ValkeyOutages"/>).
/// This overrides an <c>abortConnect</c> written in the configuration string.
/// </summary>
public static class ValkeyConnections
{
    public static IConnectionMultiplexer Connect(string configuration)
    {
        var options = ConfigurationOptions.Parse(configuration);
        options.AbortOnConnectFail = false;
        return ConnectionMultiplexer.Connect(options);
    }
}
