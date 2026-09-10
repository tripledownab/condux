// Generic string helpers, shared low in the hierarchy so features never reimplement them.

// Shorten a string to at most `max` characters, appending an ellipsis when it was longer. Keeps a long
// value (e.g. a webhook URL) from wrapping the layout on narrow screens; pair with a title tooltip so the
// full value stays reachable.
export function truncate(value: string, max: number): string {
  return value.length > max ? `${value.slice(0, max)}…` : value;
}
