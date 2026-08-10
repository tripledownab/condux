"use client";

import { useTranslations } from "next-intl";
import { type FormEvent, useState } from "react";
import { useCreateInvite } from "@/src/api/generated/condux";
import { FIELD_CLASS, SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { InviteCreatedLink } from "@/src/orgs/invite-created-link";

// The optional invite affordance inside the onboarding org step: send teammate invites one email at
// a time (the full management surface lives in settings Members).
export function InviteTeammates({ orgId }: { orgId: number }) {
  const translate = useTranslations("onboarding.invite");
  const [email, setEmail] = useState("");
  const [sent, setSent] = useState<string[]>([]);
  // The latest created invite, so its accept link can be copied and shared (the only way in with no SMTP).
  const [created, setCreated] = useState<{ email: string; token: string } | null>(null);
  const invite = useCreateInvite();

  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const trimmed = email.trim();
    if (trimmed.length === 0) {
      return;
    }
    invite.mutate(
      { orgId, data: { email: trimmed, role: "member" } },
      {
        onSuccess: (response) => {
          setSent((previous) => [...previous, trimmed]);
          if (response.status === 201) {
            setCreated({ email: response.data.email, token: response.data.token });
          }
          setEmail("");
        },
      },
    );
  };

  return (
    <div>
      <form onSubmit={submit} className="flex flex-wrap items-end gap-2">
        <label className="flex flex-col gap-1 text-xs text-muted-foreground">
          {translate("emailLabel")}
          <input
            type="email"
            value={email}
            onChange={(event) => setEmail(event.target.value)}
            placeholder={translate("emailPlaceholder")}
            className={FIELD_CLASS}
          />
        </label>
        <button type="submit" disabled={invite.isPending} className={SECONDARY_BUTTON_CLASS}>
          {invite.isPending ? translate("sending") : translate("send")}
        </button>
        {invite.isError ? <p className="w-full text-xs text-error">{translate("error")}</p> : null}
      </form>
      {sent.length > 0 ? (
        <p className="mt-2 text-xs text-muted-foreground">
          {translate("sent", { emails: sent.join(", ") })}
        </p>
      ) : null}
      {created ? <InviteCreatedLink email={created.email} token={created.token} /> : null}
    </div>
  );
}
