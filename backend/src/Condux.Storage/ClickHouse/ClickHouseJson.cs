using System.Globalization;
using System.Text.Json;

namespace Condux.Storage.ClickHouse;

/// <summary>
/// Reading scalars out of a ClickHouse <c>FORMAT JSON</c> response, where the JSON type of a number
/// depends on its SQL type: 64-bit integers arrive quoted (a UInt64 <c>sum</c> is a string) and 32-bit
/// ones arrive bare (<c>toUnixTimestamp</c> is a UInt32 number). A reader that handles only the kind
/// its own query happens to produce keeps working until somebody adds a column of the other width, so
/// every integer is read as either.
/// </summary>
internal static class ClickHouseJson
{
    public static bool TryReadInt64(JsonElement element, out long value)
    {
        value = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(
                element.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }
}
