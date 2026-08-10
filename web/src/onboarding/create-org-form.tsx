"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { type FormEvent, useState } from "react";
import { getListMyOrgsQueryKey, useCreateOrg } from "@/src/api/generated/condux";
import { FIELD_CLASS, PRIMARY_BUTTON_CLASS } from "@/src/components/form";
import { slugify } from "@/src/lib/strings";

// The onboarding create-org step (ADR-0018): signup mints no org, so the first thing a new user
// does is name their organization. The slug derives from the name.
export function CreateOrgForm() {
  const translate = useTranslations("onboarding.org");
  const queryClient = useQueryClient();
  const [name, setName] = useState("");
  const createOrg = useCreateOrg({
    mutation: {
      onSuccess: () => queryClient.invalidateQueries({ queryKey: getListMyOrgsQueryKey() }),
    },
  });

  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const trimmed = name.trim();
    if (trimmed.length === 0) {
      return;
    }
    createOrg.mutate({ data: { name: trimmed, slug: slugify(trimmed), tier: null } });
  };

  return (
    <form onSubmit={submit} className="flex flex-wrap items-end gap-2">
      <label className="flex flex-col gap-1 text-xs text-muted-foreground">
        {translate("nameLabel")}
        <input
          value={name}
          onChange={(event) => setName(event.target.value)}
          placeholder={translate("namePlaceholder")}
          className={FIELD_CLASS}
          required
        />
      </label>
      <button type="submit" disabled={createOrg.isPending} className={PRIMARY_BUTTON_CLASS}>
        {createOrg.isPending ? translate("creating") : translate("create")}
      </button>
      {createOrg.isError ? <p className="w-full text-xs text-error">{translate("error")}</p> : null}
    </form>
  );
}
