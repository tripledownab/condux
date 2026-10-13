using Condux.Core.FixEngine;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>The nullable <c>failure_reason</c> column both run tables carry, read and written one way.</summary>
internal static class FixFailureColumn
{
    public static FixFailureReason? Read(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : (FixFailureReason)reader.GetInt16(ordinal);

    public static object ToParameter(FixFailureReason? reason) =>
        reason is { } value ? (short)value : DBNull.Value;
}
