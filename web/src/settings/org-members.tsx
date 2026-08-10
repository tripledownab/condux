"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import {
  getListMembersQueryKey,
  useListMembers,
  useMe,
  useRemoveMember,
  useUpdateMemberRole,
} from "@/src/api/generated/condux";
import type { OrgMemberResponse } from "@/src/api/generated/model";
import { SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { Notice } from "@/src/components/notice";
import { Combobox } from "@/src/components/ui/combobox";
import { OrgStatus, useCurrentOrg } from "@/src/orgs/current-org";
import { OrgInvites } from "./org-invites";

// The role a member can be set to (owner outranks admin outranks member).
const ROLE_OPTIONS = ["member", "admin", "owner"];

// The Members settings tab: list the org's members with their roles, and (for owners) change a role or
// remove a member. Admins and owners also get the invite section (OrgInvites). Reads need member+, role
// changes and removals need owner; the backend enforces this and the UI hides what the caller cannot do.
export function OrgMembers() {
  const translate = useTranslations("settings.members");
  const current = useCurrentOrg();

  if (current.status === OrgStatus.Loading) {
    return <Notice>{translate("loading")}</Notice>;
  }
  if (current.status === OrgStatus.Error) {
    return <Notice>{translate("error")}</Notice>;
  }
  if (current.status === OrgStatus.NoOrg) {
    return <Notice>{translate("noOrg")}</Notice>;
  }

  const { org, role } = current;
  const canInvite = role === "owner" || role === "admin";
  const canManage = role === "owner";

  return (
    <div className="flex flex-col gap-8">
      <section>
        <h2 className="font-heading text-lg font-semibold text-foreground">{translate("title")}</h2>
        <p className="mt-1 text-sm text-muted-foreground">{translate("description")}</p>
        <div className="mt-4">
          <MemberList orgId={org.id} canManage={canManage} />
        </div>
      </section>
      {canInvite ? <OrgInvites orgId={org.id} /> : null}
    </div>
  );
}

function MemberList({ orgId, canManage }: { orgId: number; canManage: boolean }) {
  const translate = useTranslations("settings.members");
  const members = useListMembers(orgId);
  const me = useMe();
  const myUserId = me.data?.data?.id;

  if (members.isPending) {
    return <Notice>{translate("loading")}</Notice>;
  }
  if (members.isError) {
    return <Notice>{translate("error")}</Notice>;
  }
  const list = members.data?.data ?? [];
  if (list.length === 0) {
    return <Notice>{translate("empty")}</Notice>;
  }

  return (
    <ul className="flex flex-col gap-2">
      {list.map((member) => (
        <MemberRow
          key={member.userId}
          orgId={orgId}
          member={member}
          isSelf={member.userId === myUserId}
          canManage={canManage}
        />
      ))}
    </ul>
  );
}

function MemberRow({
  orgId,
  member,
  isSelf,
  canManage,
}: {
  orgId: number;
  member: OrgMemberResponse;
  isSelf: boolean;
  canManage: boolean;
}) {
  const translate = useTranslations("settings.members");
  const translateRole = useTranslations("settings.members.roles");
  const tCommon = useTranslations("common");
  const queryClient = useQueryClient();
  const updateRole = useUpdateMemberRole();
  const removeMember = useRemoveMember();

  const invalidate = () =>
    queryClient.invalidateQueries({ queryKey: getListMembersQueryKey(orgId) });

  // Owners act on other members; nobody acts on their own row (no self-demote or self-remove).
  const editable = canManage && !isSelf;
  const roleOptions = ROLE_OPTIONS.map((option) => ({
    value: option,
    label: translateRole(option),
  }));

  return (
    <li className="flex items-center justify-between gap-3 rounded-lg border border-border bg-card p-4">
      <div className="min-w-0">
        <span className="text-sm text-foreground">{member.email}</span>
        {isSelf ? (
          <span className="ml-2 text-xs text-muted-foreground">{translate("you")}</span>
        ) : null}
      </div>
      <div className="flex items-center gap-3">
        {editable ? (
          <Combobox
            value={member.role}
            onValueChange={(role) =>
              updateRole.mutate(
                { orgId, userId: member.userId, data: { role } },
                { onSuccess: invalidate },
              )
            }
            options={roleOptions}
            aria-label={translate("role")}
            searchPlaceholder={tCommon("comboboxSearch")}
            emptyText={tCommon("comboboxEmpty")}
          />
        ) : (
          <span className="rounded bg-secondary px-1.5 py-0.5 text-xs text-muted-foreground">
            {translateRole(member.role)}
          </span>
        )}
        {editable ? (
          <button
            type="button"
            onClick={() =>
              removeMember.mutate({ orgId, userId: member.userId }, { onSuccess: invalidate })
            }
            disabled={removeMember.isPending}
            className={SECONDARY_BUTTON_CLASS}
          >
            {translate("remove")}
          </button>
        ) : null}
      </div>
    </li>
  );
}
