// Generic time-formatting helpers, shared low in the hierarchy so features never reimplement them.

// Compact relative time ("5s", "3m", "2h", "4d"); falls back to the ISO date for anything older than a
// week. `now` is injectable so the output is deterministic in tests.
export function formatRelativeTime(iso: string, now: Date = new Date()): string {
  const seconds = Math.max(0, Math.round((now.getTime() - new Date(iso).getTime()) / 1000));
  if (seconds < 60) {
    return `${seconds}s`;
  }
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) {
    return `${minutes}m`;
  }
  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    return `${hours}h`;
  }
  const days = Math.floor(hours / 24);
  if (days < 7) {
    return `${days}d`;
  }
  return iso.slice(0, 10);
}

// An ISO timestamp trimmed to "YYYY-MM-DD HH:MM:SS" (UTC) - readable and deterministic (no locale or
// timezone drift). Returns the input unchanged if it is not the expected shape.
export function formatTimestamp(iso: string): string {
  const match = iso.match(/^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2}:\d{2})/);
  return match ? `${match[1]} ${match[2]}` : iso;
}
