"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import {
  getAdminListOrgMembersQueryKey,
  getAdminOrgDetailQueryKey,
  useAdminListOrgMembers,
  useAdminRemoveMember,
  useAdminUpdateMemberRole,
} from "@/src/api/generated/condux";
import type { OrgMemberResponse } from "@/src/api/generated/model";
import { ConfirmDialog } from "@/src/components/confirm-dialog";
import { SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { Notice } from "@/src/components/notice";
import { Combobox } from "@/src/components/ui/combobox";

const ROLE_OPTIONS = ["member", "admin", "owner"];

// Cross-tenant member management for the admin detail page (ADR-0027): change a role or remove a member.
// The operator is not a member, so there is no self-row gating; the backend still guards the last owner.
export function AdminOrgMembers({ orgId }: { orgId: number }) {
  const translate = useTranslations("admin.orgDetail.members");
  const members = useAdminListOrgMembers(orgId);

  return (
    <section>
      <h2 className="font-heading text-lg font-semibold text-foreground">{translate("title")}</h2>
      <div className="mt-3">
        {members.isPending ? (
          <Notice>{translate("loading")}</Notice>
        ) : members.isError ? (
          <Notice>{translate("error")}</Notice>
        ) : (members.data?.data ?? []).length === 0 ? (
          <Notice>{translate("empty")}</Notice>
        ) : (
          <ul className="flex flex-col gap-2">
            {(members.data?.data ?? []).map((member) => (
              <MemberRow key={member.userId} orgId={orgId} member={member} />
            ))}
          </ul>
        )}
      </div>
    </section>
  );
}

function MemberRow({ orgId, member }: { orgId: number; member: OrgMemberResponse }) {
  const translate = useTranslations("admin.orgDetail.members");
  const translateRole = useTranslations("admin.orgDetail.members.roles");
  const tCommon = useTranslations("common");
  const queryClient = useQueryClient();
  const updateRole = useAdminUpdateMemberRole();
  const removeMember = useAdminRemoveMember();
  const [confirmOpen, setConfirmOpen] = useState(false);
  const roleOptions = ROLE_OPTIONS.map((option) => ({
    value: option,
    label: translateRole(option),
  }));

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: getAdminListOrgMembersQueryKey(orgId) });
    queryClient.invalidateQueries({ queryKey: getAdminOrgDetailQueryKey(orgId) });
  };

  return (
    <li className="flex items-center justify-between gap-3 rounded-lg border border-border bg-card p-4">
      <span className="min-w-0 truncate text-sm text-foreground">{member.email}</span>
      <div className="flex items-center gap-3">
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
        <button
          type="button"
          onClick={() => setConfirmOpen(true)}
          disabled={removeMember.isPending}
          className={SECONDARY_BUTTON_CLASS}
        >
          {translate("remove")}
        </button>
      </div>
      <ConfirmDialog
        open={confirmOpen}
        onOpenChange={setConfirmOpen}
        title={translate("removeConfirmTitle")}
        body={translate("removeConfirmBody", { email: member.email })}
        confirmLabel={translate("remove")}
        cancelLabel={translate("cancel")}
        pending={removeMember.isPending}
        destructive
        onConfirm={() =>
          removeMember.mutate(
            { orgId, userId: member.userId },
            {
              onSuccess: () => {
                setConfirmOpen(false);
                invalidate();
              },
            },
          )
        }
      />
    </li>
  );
}
