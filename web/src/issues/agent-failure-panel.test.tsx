import { screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";
import { AgentFailurePanel } from "./agent-failure-panel";
import { EventChips } from "./event-detail";
import { parseEventPayload } from "./event-payload";

// The tags the relay keeps on an event made from a failed tool span, as in the OpenAI Agents capture
// where a tool raised.
const toolFailure = {
  "gen_ai.operation.name": "execute_tool",
  "gen_ai.provider.name": "openai",
  "gen_ai.agent.name": "checkout-agent",
  "gen_ai.tool.name": "boom",
};

function eventWith(tags: Record<string, string>) {
  const event = parseEventPayload(
    JSON.stringify({ Exceptions: [{ Type: "ValueError", Value: "boom" }], Tags: tags }),
  );
  if (event === null) {
    throw new Error("payload should parse");
  }
  return event;
}

describe("AgentFailurePanel", () => {
  it("shows what failed and where, worded for a reader", () => {
    renderWithIntl(<AgentFailurePanel event={eventWith(toolFailure)} />);

    expect(screen.getByText("Agent failure")).toBeInTheDocument();
    expect(screen.getByText("Tool call")).toBeInTheDocument();
    expect(screen.getByText("boom")).toBeInTheDocument();
    expect(screen.getByText("checkout-agent")).toBeInTheDocument();
    expect(screen.getByText("openai")).toBeInTheDocument();
    // A field the span did not carry gets no row.
    expect(screen.queryByText("Model")).toBeNull();
  });

  it("prefers the answering model and falls back to the older provider name", () => {
    renderWithIntl(
      <AgentFailurePanel
        event={eventWith({
          "gen_ai.operation.name": "chat",
          "gen_ai.system": "openai",
          "gen_ai.request.model": "gpt-requested",
          "gen_ai.response.model": "gpt-answered",
          "error.type": "RateLimitError",
        })}
      />,
    );

    expect(screen.getByText("Model call")).toBeInTheDocument();
    expect(screen.getByText("gpt-answered")).toBeInTheDocument();
    expect(screen.queryByText("gpt-requested")).toBeNull();
    expect(screen.getByText("openai")).toBeInTheDocument();
    expect(screen.getByText("RateLimitError")).toBeInTheDocument();
  });

  it("shows an operation it has no words for as sent", () => {
    renderWithIntl(<AgentFailurePanel event={eventWith({ "gen_ai.operation.name": "rerank" })} />);

    expect(screen.getByText("rerank")).toBeInTheDocument();
  });

  it("renders nothing for an event that is not an agent's", () => {
    const { container } = renderWithIntl(
      <AgentFailurePanel event={eventWith({ "gen_ai.tool.name": "boom", region: "eu" })} />,
    );

    expect(container).toBeEmptyDOMElement();
  });
});

describe("EventChips beside the agent block", () => {
  it("leaves out the tags the block shows and keeps the rest", () => {
    renderWithIntl(<EventChips event={eventWith({ ...toolFailure, region: "eu" })} />);

    expect(screen.getByText("region: eu")).toBeInTheDocument();
    expect(screen.queryByText(/gen_ai\./)).toBeNull();
  });

  it("keeps a tag the block passed over for a preferred one", () => {
    renderWithIntl(
      <EventChips
        event={eventWith({
          "gen_ai.operation.name": "chat",
          "gen_ai.request.model": "gpt-requested",
          "gen_ai.response.model": "gpt-answered",
        })}
      />,
    );

    expect(screen.getByText("gen_ai.request.model: gpt-requested")).toBeInTheDocument();
    expect(screen.queryByText(/gen_ai\.response\.model/)).toBeNull();
  });

  it("keeps every tag of an event that is not an agent's", () => {
    renderWithIntl(<EventChips event={eventWith({ "gen_ai.tool.name": "boom" })} />);

    expect(screen.getByText("gen_ai.tool.name: boom")).toBeInTheDocument();
  });
});
