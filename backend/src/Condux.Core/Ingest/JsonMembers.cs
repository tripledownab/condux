using System.Text.Json;

namespace Condux.Core.Ingest;

/// <summary>
/// The one way the Sentry parser looks a member up.
/// </summary>
/// <remarks>
/// <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/> throws unless the element is an
/// object, and a sender chooses every kind: an array item the parser expects to be an object can be a
/// number, a string or null. Several lookups checked their element first and several did not, so a value
/// of the wrong kind escaped the parser as a 500. This answers "no such member" for anything but an
/// object, which the parser already reads as a default, and a scan test fails on a lookup that bypasses
/// it (SentryParserMemberScanTest).
/// </remarks>
internal static class JsonMembers
{
    internal static bool TryGetMember(this JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            return element.TryGetProperty(name, out value);
        }

        value = default;
        return false;
    }
}
