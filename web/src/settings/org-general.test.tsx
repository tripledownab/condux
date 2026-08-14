import { fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ConduxApiError } from "@/src/api/fetcher";
import { OrgStatus } from "@/src/orgs/current-org";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const useUpdateOrgMock = vi.fn();
const useAiFixUsageMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useUpdateOrg: () => useUpdateOrgMock(),
  useAiFixUsage: () => useAiFixUsageMock(),
  getListMyOrgsQueryKey: () => ["orgs"],
}));

const useCurrentOrgMock = vi.fn();
vi.mock("@/src/orgs/current-org", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/src/orgs/current-org")>()),
  useCurrentOrg: () => useCurrentOrgMock(),
}));
// The Plan section fetches billing over react-query; it has its own test, so stub it here to keep this
// suite focused on the AI-fix settings (and free of the react-query provider).
vi.mock("./plan-section", () => ({ PlanSection: () => null }));

import { OrgGeneral } from "./org-general";

const idleUpdate = { mutate: vi.fn(), isPending: false, isError: false, error: null };

function asOrg(role: string, aiFixMode = 0, aiFixCostCapUsd: number | null = null, tier = 2) {
  useCurrentOrgMock.mockReturnValue({
    status: OrgStatus.Ready,
    org: { id: 3, name: "Acme", slug: "acme", tier, createdAt: "", aiFixMode, aiFixCostCapUsd },
    role,
  });
}

function withUsage(monthToDateUsd: number, capUsd: number | null) {
  useAiFixUsageMock.mockReturnValue({
    data: { data: { monthToDateUsd, capUsd } },
    refetch: vi.fn(),
  });
}

beforeEach(() => {
  asOrg("admin");
  useUpdateOrgMock.mockReturnValue(idleUpdate);
  withUsage(0, null);
});

afterEach(() => vi.clearAllMocks());

describe("OrgGeneral AI-fix settings", () => {
  it("lets an admin switch to automatic, preserving the (absent) cap", () => {
    const mutate = vi.fn();
    useUpdateOrgMock.mockReturnValue({ ...idleUpdate, mutate });
    renderWithIntl(<OrgGeneral />);

    fireEvent.click(screen.getByRole("radio", { name: /Automatic/ }));
    expect(mutate).toHaveBeenCalledWith(
      { orgId: 3, data: { aiFixMode: 1, aiFixCostCapUsd: null, fixExecution: 0 } },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  it("does not re-submit when the current mode is clicked", () => {
    const mutate = vi.fn();
    useUpdateOrgMock.mockReturnValue({ ...idleUpdate, mutate });
    asOrg("admin", 1); // already auto
    renderWithIntl(<OrgGeneral />);
    fireEvent.click(screen.getByRole("radio", { name: /Automatic/ }));
    expect(mutate).not.toHaveBeenCalled();
  });

  it("lets a BYO (Enterprise) org set its own budget alongside the current mode", () => {
    const mutate = vi.fn();
    useUpdateOrgMock.mockReturnValue({ ...idleUpdate, mutate });
    asOrg("admin", 1, null, 3); // Enterprise/BYO, auto mode; the budget save must keep the mode
    renderWithIntl(<OrgGeneral />);

    fireEvent.change(screen.getByLabelText(/Budget \(USD\)/), { target: { value: "50" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(mutate).toHaveBeenCalledWith(
      { orgId: 3, data: { aiFixMode: 1, aiFixCostCapUsd: 50, fixExecution: 0 } },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  it("shows a read-only fair-use ceiling for platform tiers and an editable budget only for BYO", () => {
    // Business (platform-billed): the compute ceiling is Condux-owned, so no editable input.
    asOrg("admin", 0, null, 2);
    const business = renderWithIntl(<OrgGeneral />);
    expect(screen.getByText("AI fix compute")).toBeInTheDocument();
    expect(screen.queryByLabelText(/Budget \(USD\)/)).not.toBeInTheDocument();
    business.unmount();

    // Enterprise/BYO: an editable budget input on their own key.
    asOrg("admin", 0, null, 3);
    const enterprise = renderWithIntl(<OrgGeneral />);
    expect(screen.getByLabelText(/Budget \(USD\)/)).toBeInTheDocument();
    enterprise.unmount();

    // Free: now carries an allowance and a ceiling, so it sees the same read-only section Business
    // does. Hiding it would leave a Free org unable to see the limit it is being held to.
    asOrg("admin", 0, null, 0);
    renderWithIntl(<OrgGeneral />);
    expect(screen.getByText("AI fix compute")).toBeInTheDocument();
    expect(screen.queryByLabelText(/Budget \(USD\)/)).not.toBeInTheDocument();
  });

  it("shows month-to-date spend against the cap", () => {
    asOrg("admin", 0, 100);
    withUsage(42.5, 100);
    renderWithIntl(<OrgGeneral />);
    expect(screen.getByText("Spent this month: $42.50 of $100.00")).toBeInTheDocument();
  });

  it("shows the settings read-only for a member (no radios or inputs)", () => {
    asOrg("member", 1);
    renderWithIntl(<OrgGeneral />);
    expect(screen.queryByRole("radio")).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/Cap \(USD\)/)).not.toBeInTheDocument();
    expect(screen.getByText("Automatic")).toBeInTheDocument();
  });

  it("lets an admin move fix execution to their own runner, preserving mode and cap", () => {
    const mutate = vi.fn();
    useUpdateOrgMock.mockReturnValue({ ...idleUpdate, mutate });
    renderWithIntl(<OrgGeneral />); // Business by default, which includes self-hosting

    fireEvent.click(screen.getByRole("radio", { name: /Your own runner/ }));
    expect(mutate).toHaveBeenCalledWith(
      { orgId: 3, data: { aiFixMode: 0, aiFixCostCapUsd: null, fixExecution: 1 } },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  it("disables the runner option below the tier that includes it", () => {
    // The gate comes from @condux/plans (generated from the catalog), not a hand-kept mirror, so this
    // follows the plan facts. Disabled rather than hidden: a Free org should learn the option exists.
    // The disabled attribute is the whole mechanism (a real browser will not deliver the click), so it
    // is what gets asserted; firing a synthetic click would only measure jsdom, which ignores disabled.
    asOrg("admin", 0, null, 0); // Free
    renderWithIntl(<OrgGeneral />);

    expect(screen.getByRole("radio", { name: /Your own runner/ })).toBeDisabled();
    expect(screen.getByText(/available from the Team plan/)).toBeInTheDocument();
  });

  it("explains the runner gate distinctly when that switch 409s", () => {
    // Two different 409s with two different remedies; showing the auto-fix message for a runner
    // refusal would send the user to the wrong setting.
    useUpdateOrgMock.mockReturnValue({
      ...idleUpdate,
      isError: true,
      error: new ConduxApiError("PATCH", "/orgs/3", 409, "self_hosted_runner_requires_upgrade"),
    });
    renderWithIntl(<OrgGeneral />);
    const alert = screen.getByRole("alert");
    expect(alert).toHaveTextContent(/runner/i);
    expect(alert).not.toHaveTextContent(/automatic/i);
  });

  it("explains the plan gate when the update 409s", () => {
    useUpdateOrgMock.mockReturnValue({
      ...idleUpdate,
      isError: true,
      error: new ConduxApiError("PATCH", "/orgs/3", 409, "ai_fixes_requires_upgrade"),
    });
    renderWithIntl(<OrgGeneral />);
    // The 409 is specifically the auto-mode refusal, so it must not read as a generic save failure.
    // Asserting the mapping rather than the wording keeps this from breaking on every copy edit.
    const alert = screen.getByRole("alert");
    expect(alert).toHaveTextContent(/automatic/i);
    expect(alert).not.toHaveTextContent(/try again/i);
  });
});
