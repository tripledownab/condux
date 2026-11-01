import { fireEvent, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ConduxApiError } from "@/src/api/fetcher";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const saveMutate = vi.fn();
const saveMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useListLinearLabels: () => ({
    data: {
      status: 200,
      data: [
        { id: "l-bug", name: "Bug", groupId: null },
        { id: "l-be", name: "Backend", groupId: "area" },
        { id: "l-fe", name: "Frontend", groupId: "area" },
        { id: "l-p1", name: "Urgent", groupId: null },
      ],
    },
    isPending: false,
    isError: false,
  }),
  useSetLinearMapping: () => saveMock(),
  getGetLinearProjectQueryKey: () => ["linear"],
  useGetLinearProject: () => statusMock(),
}));
const statusMock = vi.fn();

import { LinearTicketSettings } from "./linear-ticket-settings";

const MAPPING = {
  teamId: "team-1",
  teamName: "Engineering",
  needsAttention: false,
  autoCreate: false,
  labels: [{ id: "l-bug", name: "Bug" }],
};

function status(autoCreatedToday: number) {
  return { data: { status: 200, data: { autoCreatedToday, autoCreateDailyLimit: 50 } } };
}

beforeEach(() => {
  saveMutate.mockClear();
  saveMock.mockReturnValue({ mutate: saveMutate, isPending: false, isError: false, error: null });
  statusMock.mockReturnValue(status(0));
});

describe("LinearTicketSettings", () => {
  // The API takes the whole choice, so every change sends the team, all labels and the checkbox together.
  it("turns auto-create on without dropping the labels", () => {
    renderWithIntl(<LinearTicketSettings projectId={1} mapping={MAPPING} canManage />);

    fireEvent.click(screen.getByRole("checkbox"));

    expect(saveMutate).toHaveBeenCalledWith(
      { projectId: 1, data: { teamId: "team-1", labelIds: ["l-bug"], autoCreate: true } },
      expect.anything(),
    );
  });

  it("removes one label and keeps the rest of the choice", () => {
    renderWithIntl(
      <LinearTicketSettings projectId={1} mapping={{ ...MAPPING, autoCreate: true }} canManage />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Remove Bug" }));

    expect(saveMutate).toHaveBeenCalledWith(
      { projectId: 1, data: { teamId: "team-1", labelIds: [], autoCreate: true } },
      expect.anything(),
    );
  });

  it("shows a member the settings but lets them change none of it", () => {
    renderWithIntl(<LinearTicketSettings projectId={1} mapping={MAPPING} canManage={false} />);

    expect(screen.getByText("Bug")).toBeInTheDocument();
    expect(screen.getByRole("checkbox")).toBeDisabled();
    expect(screen.queryByRole("button", { name: "Remove Bug" })).not.toBeInTheDocument();
    expect(screen.queryByRole("combobox")).not.toBeInTheDocument();
  });

  it("shows today's automatic count, and says when the limit stops new ones", () => {
    statusMock.mockReturnValue(status(12));
    renderWithIntl(
      <LinearTicketSettings projectId={1} mapping={{ ...MAPPING, autoCreate: true }} canManage />,
    );
    expect(screen.getByText(/12 of today.s 50 automatic Linear issues used/)).toBeInTheDocument();

    statusMock.mockReturnValue(status(50));
    renderWithIntl(
      <LinearTicketSettings projectId={1} mapping={{ ...MAPPING, autoCreate: true }} canManage />,
    );
    expect(screen.getByText(/limit of 50 is reached/)).toBeInTheDocument();
  });

  // A label deleted in Linear stays listed until the next save drops it, marked so a failure has a reason.
  it("marks a label Linear no longer offers", () => {
    renderWithIntl(
      <LinearTicketSettings
        projectId={1}
        mapping={{ ...MAPPING, labels: [{ id: "l-gone", name: "Old" }] }}
        canManage
      />,
    );
    expect(screen.getByText("(no longer in Linear)")).toBeInTheDocument();
  });

  // A group lets an issue carry one of its labels, so once the project uses Backend, Frontend is not offered.
  it("does not offer a second label from a group the project already uses", async () => {
    renderWithIntl(
      <LinearTicketSettings
        projectId={1}
        mapping={{ ...MAPPING, labels: [{ id: "l-be", name: "Backend" }] }}
        canManage
      />,
    );
    fireEvent.click(screen.getByRole("combobox"));
    expect(await screen.findByRole("option", { name: /Urgent/ })).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: /Frontend/ })).not.toBeInTheDocument();
  });

  it("names the label problem a save was refused for", () => {
    saveMock.mockReturnValue({
      mutate: saveMutate,
      isPending: false,
      isError: true,
      error: new ConduxApiError("PUT", "/x", 400, "linear_labels_same_group"),
    });
    renderWithIntl(<LinearTicketSettings projectId={1} mapping={MAPPING} canManage />);
    expect(screen.getByText(/same Linear label group/)).toBeInTheDocument();
  });
});
