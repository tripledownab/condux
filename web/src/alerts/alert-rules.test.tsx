import { fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const useListAlertRulesMock = vi.fn();
const createRuleMutate = vi.fn();
const updateRuleMutate = vi.fn();
const updateChannelMutate = vi.fn();
const testChannelMutate = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useListAlertRules: () => useListAlertRulesMock(),
  useCreateAlertRule: () => ({ mutate: createRuleMutate, isPending: false }),
  useUpdateAlertRule: () => ({ mutate: updateRuleMutate, isPending: false }),
  useDeleteAlertRule: () => ({ mutate: vi.fn(), isPending: false }),
  useAddAlertChannel: () => ({ mutate: vi.fn(), isPending: false }),
  useUpdateAlertChannel: () => ({ mutate: updateChannelMutate, isPending: false }),
  useDeleteAlertChannel: () => ({ mutate: vi.fn(), isPending: false }),
  useTestAlertChannel: () => ({ mutate: testChannelMutate, isPending: false }),
  getListAlertRulesQueryKey: () => ["alert-rules"],
}));

import { AlertRules } from "./alert-rules";

function rule() {
  return {
    id: "0192f000-0000-7000-8000-000000000000",
    name: "prod errors",
    events: [1],
    levels: [4, 5],
    enabled: true,
    channels: [{ id: "c1", channel: 2, target: "https://hooks.slack.test/x" }],
  };
}

afterEach(() => vi.clearAllMocks());

describe("AlertRules", () => {
  it("prompts to create a rule when there are none", () => {
    useListAlertRulesMock.mockReturnValue({ isPending: false, isError: false, data: { data: [] } });
    renderWithIntl(<AlertRules projectId={7} />);
    expect(screen.getByRole("heading", { name: "Alert rules" })).toBeInTheDocument();
    expect(screen.getByText("No alert rules yet. Create one to get notified.")).toBeInTheDocument();
  });

  it("creates a rule from the form", () => {
    useListAlertRulesMock.mockReturnValue({ isPending: false, isError: false, data: { data: [] } });
    renderWithIntl(<AlertRules projectId={7} />);

    fireEvent.change(screen.getByPlaceholderText("Production errors"), {
      target: { value: "API errors" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Create rule" }));

    expect(createRuleMutate).toHaveBeenCalledWith(
      {
        projectId: 7,
        data: { name: "API errors", events: [1, 2], levels: [4, 5] },
      },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  it("renders an existing rule with its channel and controls", () => {
    useListAlertRulesMock.mockReturnValue({
      isPending: false,
      isError: false,
      data: { data: [rule()] },
    });
    renderWithIntl(<AlertRules projectId={7} />);

    expect(screen.getByText("prod errors")).toBeInTheDocument();
    // The channel type shows as a badge in the row; its target lives in the expandable panel.
    expect(screen.getByText("Slack")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Disable" })).toBeInTheDocument();
  });

  it("sends a test to a rule's channel", () => {
    useListAlertRulesMock.mockReturnValue({
      isPending: false,
      isError: false,
      data: { data: [rule()] },
    });
    renderWithIntl(<AlertRules projectId={7} />);

    // Channels live in the expandable panel; open it, then send the test.
    fireEvent.click(screen.getByRole("button", { name: "Toggle channels" }));
    fireEvent.click(screen.getByRole("button", { name: "Send test" }));
    expect(testChannelMutate).toHaveBeenCalledWith(
      {
        projectId: 7,
        ruleId: "0192f000-0000-7000-8000-000000000000",
        channelId: "c1",
      },
      expect.objectContaining({ onSuccess: expect.any(Function), onError: expect.any(Function) }),
    );
  });

  it("edits a rule's settings, carrying the unchanged fields + enabled", async () => {
    useListAlertRulesMock.mockReturnValue({
      isPending: false,
      isError: false,
      data: { data: [rule()] },
    });
    renderWithIntl(<AlertRules projectId={7} />);

    // Edit opens the rule modal (channels are collapsed, so the rule's Edit is the only one). Await the
    // dialog portal before reading its input.
    fireEvent.click(screen.getByRole("button", { name: "Edit" }));
    fireEvent.change(await screen.findByDisplayValue("prod errors"), {
      target: { value: "critical only" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(updateRuleMutate).toHaveBeenCalledWith(
      {
        projectId: 7,
        ruleId: "0192f000-0000-7000-8000-000000000000",
        data: {
          name: "critical only",
          events: [1],
          levels: [4, 5],
          enabled: true,
        },
      },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  it("edits a channel's target", () => {
    useListAlertRulesMock.mockReturnValue({
      isPending: false,
      isError: false,
      data: { data: [rule()] },
    });
    renderWithIntl(<AlertRules projectId={7} />);

    // Open the channel panel, then edit the channel (rule Edit is [0], the channel's is [1]).
    fireEvent.click(screen.getByRole("button", { name: "Toggle channels" }));
    fireEvent.click(screen.getAllByRole("button", { name: "Edit" })[1]);
    fireEvent.change(screen.getByDisplayValue("https://hooks.slack.test/x"), {
      target: { value: "https://hooks.slack.test/y" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(updateChannelMutate).toHaveBeenCalledWith(
      {
        projectId: 7,
        ruleId: "0192f000-0000-7000-8000-000000000000",
        channelId: "c1",
        data: {
          channel: 2,
          target: "https://hooks.slack.test/y",
          template: null,
        },
      },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });
});
