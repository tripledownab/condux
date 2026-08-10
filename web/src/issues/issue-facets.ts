// Aggregates the issue's sampled events into the facets panel data: how many distinct users were
// affected and the top values per dimension (contexts like browser/os plus every tag key). Proportions
// from a sample are statistically sound even though absolute totals are not — the UI labels the panel
// as sample-based.
import type { ParsedEvent } from "./event-payload";

export type FacetValue = { value: string; share: number };

export type Facet = { key: string; values: FacetValue[] };

const TOP_VALUES = 3;

// Distinct pseudonymous user keys across the sampled events (empty keys are events with no user).
export function usersAffected(events: ParsedEvent[]): number {
  return new Set(events.map((event) => event.userKey).filter((key) => key.length > 0)).size;
}

export function buildFacets(events: ParsedEvent[]): Facet[] {
  if (events.length === 0) {
    return [];
  }

  // dimension key -> value -> occurrences, from contexts and tags alike.
  const counts = new Map<string, Map<string, number>>();
  for (const event of events) {
    for (const source of [event.contexts, event.tags]) {
      for (const [key, value] of Object.entries(source)) {
        const values = counts.get(key) ?? new Map<string, number>();
        values.set(value, (values.get(value) ?? 0) + 1);
        counts.set(key, values);
      }
    }
  }

  return [...counts.entries()]
    .map(([key, values]) => {
      const total = [...values.values()].reduce((sum, count) => sum + count, 0);
      return {
        key,
        values: [...values.entries()]
          .sort((a, b) => b[1] - a[1])
          .slice(0, TOP_VALUES)
          .map(([value, count]) => ({ value, share: count / total })),
      };
    })
    .sort((a, b) => a.key.localeCompare(b.key));
}
