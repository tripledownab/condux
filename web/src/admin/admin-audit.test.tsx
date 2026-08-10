import { screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const auditMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({ useAdminAuditLog: () => auditMock() }));

import { AdminAudit } from "./admin-audit";

afterEach(() => vi.clearAllMocks());

describe("AdminAudit", () => {
  it("renders a row per audit entry", () => {
    auditMock.mockReturnValue({
      isPending: false,
      isError: false,
      data: {
        data: [
          {
            id: 1,
            actorId: 5,
            actorEmail: "boss@condux.test",
            action: "org.rename",
            targetOrgId: 3,
            targetUserId: null,
            details: { before: "Old", after: "New" },
            createdAt: "2026-01-02T03:04:05Z",
          },
        ],
      },
    });
    renderWithIntl(<AdminAudit />);

    expect(screen.getByText("boss@condux.test")).toBeInTheDocument();
    expect(screen.getByText("org.rename")).toBeInTheDocument();
  });

  it("shows an empty notice when there are no actions", () => {
    auditMock.mockReturnValue({ isPending: false, isError: false, data: { data: [] } });
    renderWithIntl(<AdminAudit />);
    expect(screen.getByText("No admin actions recorded yet.")).toBeInTheDocument();
  });
});
