// Reads an AI agent's failure out of an event's tags: what failed (a model call, a tool, an agent run),
// on which provider, model, agent and tool, and the error type the instrumentation named. The relay keeps
// these OpenTelemetry GenAI attributes as tags on an event made from a failed span (OtlpSpanTags).

import type { ParsedEvent } from "./event-payload";

export type AgentFailure = {
  operation: string;
  provider?: string;
  model?: string;
  agent?: string;
  tool?: string;
  errorType?: string;
  // The tag keys the values above were read from, so the chip row can leave exactly those out.
  shownTags: ReadonlySet<string>;
};

const Operation = "gen_ai.operation.name";

// Each field with the tags that may carry it, the preferred one first. gen_ai.system is the older name of
// the provider, and the response model, when present, is the one that actually answered.
const Fields = {
  provider: ["gen_ai.provider.name", "gen_ai.system"],
  model: ["gen_ai.response.model", "gen_ai.request.model"],
  agent: ["gen_ai.agent.name"],
  tool: ["gen_ai.tool.name"],
  errorType: ["error.type"],
} as const;

// The failure, or null when the event carries no GenAI operation and so is not an agent's.
export function toAgentFailure(event: ParsedEvent): AgentFailure | null {
  const operation = event.tags[Operation];
  if (!operation) {
    return null;
  }
  const shownTags = new Set([Operation]);
  const failure: AgentFailure = { operation, shownTags };
  for (const [field, keys] of Object.entries(Fields) as [
    keyof typeof Fields,
    readonly string[],
  ][]) {
    const key = keys.find((candidate) => event.tags[candidate]);
    if (key !== undefined) {
      failure[field] = event.tags[key];
      shownTags.add(key);
    }
  }
  return failure;
}
