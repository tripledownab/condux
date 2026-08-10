"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import {
  getListInvitesQueryKey,
  useCreateInvite,
  useListInvites,
  useRevokeInvite,
} from "@/src/api/generated/condux";
import { FIELD_CLASS, PRIMARY_BUTTON_CLASS, SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { Notice } from "@/src/components/notice";
import { Combobox } from "@/src/components/ui/combobox";
import { formatTimestamp } from "@/src/lib/time";
import { InviteCreatedLink } from "@/src/orgs/invite-created-link";

// You cannot invite someone as owner; ownership is granted by promoting a member on the members list.
const INVITE_ROLES = ["member", "admin"];

// The invite section shown to admins and owners: invite a new member by email and role, and manage the
// pending invites. Reuses the existing control-plane invite API (createInvite, listInvites, revokeInvite).
export function OrgInvites({ orgId }: { orgId: number }) {
  const translate = useTranslations("settings.members");
  return (
    <section>
      <h2 className="font-heading text-lg font-semibold text-foreground">
        {translate("inviteTitle")}
      </h2>
      <p className="mt-1 text-sm text-muted-foreground">{translate("inviteDescription")}</p>
      <div className="mt-4">
        <InviteForm orgId={orgId} />
      </div>
      <div className="mt-4">
        <PendingInvites orgId={orgId} />
      </div>
    </section>
  );
}

function InviteForm({ orgId }: { orgId: number }) {
  const translate = useTranslations("settings.members");
  const translateRole = useTranslations("settings.members.roles");
  const tCommon = useTranslations("common");
  const queryClient = useQueryClient();
  const createInvite = useCreateInvite();
  const [email, setEmail] = useState("");
  const [role, setRole] = useState("member");
  const roleOptions = INVITE_ROLES.map((option) => ({
    value: option,
    label: translateRole(option),
  }));
  // The most recently created invite, so its accept link can be copied (the token is returned only once,
  // and it is the only way in when email delivery is off).
  const [created, setCreated] = useState<{ email: string; token: string } | null>(null);

  const submit = (event: React.FormEvent) => {
    event.preventDefault();
    if (email.trim() === "") {
      return;
    }
    createInvite.mutate(
      { orgId, data: { email: email.trim(), role } },
      {
        onSuccess: (response) => {
          if (response.status === 201) {
            setCreated({ email: response.data.email, token: response.data.token });
          }
          setEmail("");
          queryClient.invalidateQueries({ queryKey: getListInvitesQueryKey(orgId) });
        },
      },
    );
  };

  return (
    <>
      <form onSubmit={submit} className="flex flex-wrap items-end gap-2">
        <input
          type="email"
          value={email}
          onChange={(event) => setEmail(event.target.value)}
          placeholder={translate("emailPlaceholder")}
          aria-label={translate("email")}
          className={FIELD_CLASS}
        />
        <Combobox
          value={role}
          onValueChange={setRole}
          options={roleOptions}
          aria-label={translate("role")}
          searchPlaceholder={tCommon("comboboxSearch")}
          emptyText={tCommon("comboboxEmpty")}
        />
        <button type="submit" disabled={createInvite.isPending} className={PRIMARY_BUTTON_CLASS}>
          {createInvite.isPending ? translate("inviting") : translate("invite")}
        </button>
        {createInvite.isError ? (
          <p className="w-full text-sm text-error">{translate("inviteError")}</p>
        ) : null}
      </form>
      {created ? <InviteCreatedLink email={created.email} token={created.token} /> : null}
    </>
  );
}

function PendingInvites({ orgId }: { orgId: number }) {
  const translate = useTranslations("settings.members");
  const translateRole = useTranslations("settings.members.roles");
  const queryClient = useQueryClient();
  const invites = useListInvites(orgId);
  const revoke = useRevokeInvite();

  const invalidate = () =>
    queryClient.invalidateQueries({ queryKey: getListInvitesQueryKey(orgId) });

  if (invites.isPending) {
    return <p className="text-xs text-muted-foreground">{translate("loadingInvites")}</p>;
  }
  if (invites.isError) {
    return <Notice>{translate("error")}</Notice>;
  }
  const list = invites.data?.data ?? [];
  if (list.length === 0) {
    return <p className="text-xs text-muted-foreground">{translate("noInvites")}</p>;
  }

  return (
    <ul className="flex flex-col gap-2">
      {list.map((invite) => (
        <li
          key={invite.id}
          className="flex items-center justify-between gap-3 rounded-lg border border-border bg-card p-3 text-sm"
        >
          <span className="text-foreground">
            {invite.email}{" "}
            <span className="text-xs text-muted-foreground">{translateRole(invite.role)}</span>
          </span>
          <div className="flex items-center gap-3">
            <span className="text-xs text-muted-foreground">
              {translate("expires", { time: formatTimestamp(invite.expiresAt) })}
            </span>
            <button
              type="button"
              onClick={() =>
                revoke.mutate({ orgId, inviteId: invite.id }, { onSuccess: invalidate })
              }
              disabled={revoke.isPending}
              className={SECONDARY_BUTTON_CLASS}
            >
              {translate("revoke")}
            </button>
          </div>
        </li>
      ))}
    </ul>
  );
}
