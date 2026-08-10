"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import {
  getGetIssueQueryKey,
  getListIssuesQueryKey,
  useAssignIssue,
  useListMembers,
  useUpdateIssueStatus,
} from "@/src/api/generated/condux";
import { Button } from "@/src/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/src/components/ui/dropdown-menu";
import { useKeyboardShortcuts } from "@/src/lib/use-keyboard-shortcuts";

// Issue statuses (Postgres: 1 unresolved, 2 resolved, 3 ignored) as the triage targets.
enum IssueStatus {
  Unresolved = 1,
  Resolved = 2,
  Ignored = 3,
}

// The quick actions leading the issue rail (layout 30): resolve and ignore toggle the status (the
// views rail moves the issue between views on the next fetch), assign waits on the assignee model.
// The Conductor's Suggest fix lives in the fix panel below.
export function IssueActions({
  projectId,
  issueId,
  status,
  orgId,
  assigneeUserId,
}: {
  projectId: number;
  issueId: string;
  status: number;
  orgId: number;
  assigneeUserId: number | null;
}) {
  const translate = useTranslations("issues.actions");
  const queryClient = useQueryClient();
  const invalidate = async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: getGetIssueQueryKey(projectId, issueId) }),
      queryClient.invalidateQueries({ queryKey: getListIssuesQueryKey(projectId) }),
    ]);
  };
  const update = useUpdateIssueStatus({ mutation: { onSuccess: invalidate } });
  const assign = useAssignIssue({ mutation: { onSuccess: invalidate } });
  const members = useListMembers(orgId);
  const memberList = members.data?.data ?? [];

  const [assignOpen, setAssignOpen] = useState(false);

  const setStatus = (next: IssueStatus) => {
    if (!update.isPending) {
      update.mutate({ projectId, issueId, data: { status: next } });
    }
  };

  const resolved = status === IssueStatus.Resolved;
  const ignored = status === IssueStatus.Ignored;
  const toggleResolved = () => setStatus(resolved ? IssueStatus.Unresolved : IssueStatus.Resolved);
  const toggleIgnored = () => setStatus(ignored ? IssueStatus.Unresolved : IssueStatus.Ignored);
  // Keyboard triage on the open issue: e resolve, i ignore, a assign. The shortcut hook ignores these
  // while a field (e.g. the notes box) is focused, so typing never toggles status.
  useKeyboardShortcuts({ e: toggleResolved, i: toggleIgnored, a: () => setAssignOpen(true) });

  return (
    <section>
      <h2 className="text-xs font-medium uppercase text-muted-foreground">{translate("title")}</h2>
      <div className="mt-2 flex flex-col gap-2">
        <Button variant="secondary" disabled={update.isPending} onClick={toggleResolved}>
          {resolved ? translate("unresolve") : translate("resolve")}
        </Button>
        <Button variant="secondary" disabled={update.isPending} onClick={toggleIgnored}>
          {ignored ? translate("unignore") : translate("ignore")}
        </Button>
        <DropdownMenu open={assignOpen} onOpenChange={setAssignOpen}>
          <DropdownMenuTrigger asChild>
            <Button variant="secondary" disabled={assign.isPending}>
              {translate("assign")}
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="start">
            {memberList.map((member) => (
              <DropdownMenuItem
                key={member.userId}
                disabled={member.userId === assigneeUserId}
                onSelect={() =>
                  assign.mutate({ projectId, issueId, data: { userId: member.userId } })
                }
              >
                {member.email}
              </DropdownMenuItem>
            ))}
            {assigneeUserId !== null ? (
              <>
                <DropdownMenuSeparator />
                <DropdownMenuItem
                  onSelect={() => assign.mutate({ projectId, issueId, data: { userId: null } })}
                >
                  {translate("unassign")}
                </DropdownMenuItem>
              </>
            ) : null}
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
    </section>
  );
}
