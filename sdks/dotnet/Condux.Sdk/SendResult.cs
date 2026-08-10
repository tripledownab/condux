namespace Condux.Sdk;

/// <summary>The outcome of a delivery attempt sequence. Reporting never throws — inspect
/// <see cref="Ok"/>. <see cref="Status"/> is the last HTTP status seen (null if every attempt was a
/// network error); <see cref="Error"/> is the last transport error message.</summary>
public sealed record SendResult(bool Ok, int Attempts, int? Status = null, string? Error = null);
