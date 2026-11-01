"use client";

// The issue page's agent block: for an event made from an AI agent's failed span, what failed and where,
// laid out like the impact block above it. Renders nothing for any other event.

import { useTranslations } from "next-intl";
import { toAgentFailure } from "./agent-failure";
import type { ParsedEvent } from "./event-payload";

// The OpenTelemetry GenAI operation names the block words for a reader; any other is shown as sent.
const OperationKeys = new Map<string, "chat" | "embeddings" | "tool" | "agent">([
  ["chat", "chat"],
  ["text_completion", "chat"],
  ["generate_content", "chat"],
  ["embeddings", "embeddings"],
  ["execute_tool", "tool"],
  ["invoke_agent", "agent"],
  ["create_agent", "agent"],
]);

export function AgentFailurePanel({ event }: { event: ParsedEvent }) {
  const translate = useTranslations("issues.agent");
  const failure = toAgentFailure(event);
  if (failure === null) {
    return null;
  }
  const operationKey = OperationKeys.get(failure.operation);
  const rows = [
    ["operation", operationKey ? translate(`operations.${operationKey}`) : failure.operation],
    ["tool", failure.tool],
    ["agent", failure.agent],
    ["model", failure.model],
    ["provider", failure.provider],
    ["errorType", failure.errorType],
  ] as const;

  return (
    <section>
      <h2 className="text-xs font-medium uppercase text-muted-foreground">{translate("title")}</h2>
      <dl className="mt-2 grid grid-cols-2 gap-y-2 text-sm">
        {rows.map(([field, value]) =>
          value ? (
            <div key={field} className="contents">
              <dt className="text-xs uppercase text-muted-foreground">{translate(field)}</dt>
              <dd className="min-w-0 truncate text-right font-mono text-foreground" title={value}>
                {value}
              </dd>
            </div>
          ) : null,
        )}
      </dl>
    </section>
  );
}
