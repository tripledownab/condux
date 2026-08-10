"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { type FormEvent, useId, useState } from "react";
import { getListProjectsQueryKey, useCreateProject } from "@/src/api/generated/condux";
import { FIELD_CLASS, Field, PRIMARY_BUTTON_CLASS } from "@/src/components/form";
import { Combobox } from "@/src/components/ui/combobox";
import { DEFAULT_PLATFORM, PLATFORM_KEYS, usePlatformLabel } from "@/src/projects/platforms";

// Shown when the org has no project yet. Creating one mints a DSN; invalidating the projects list lets
// the settings page re-resolve to the "ready" view (project info + keys, with the new DSN).
export function CreateProjectForm({ orgId }: { orgId: number }) {
  const translate = useTranslations("settings.createProject");
  const platformLabel = usePlatformLabel();
  const queryClient = useQueryClient();
  const nameId = useId();
  const platformId = useId();
  const [name, setName] = useState("");
  const [platform, setPlatform] = useState<string>(DEFAULT_PLATFORM);
  const createProject = useCreateProject();
  const platformOptions = PLATFORM_KEYS.map((key) => ({ value: key, label: platformLabel(key) }));

  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    createProject.mutate(
      { orgId, data: { name, platform } },
      {
        onSuccess: () =>
          queryClient.invalidateQueries({ queryKey: getListProjectsQueryKey(orgId) }),
      },
    );
  };

  const errorMessage = createProject.isError ? translate("error") : null;

  // A tight inline creator: name, platform and the submit button share one row (wrapping on narrow
  // screens). No card — on the projects page it sits under the list, and in onboarding the step already
  // provides the surrounding title + card.
  return (
    <form onSubmit={submit} noValidate>
      <div className="flex flex-wrap items-end gap-3">
        <Field id={nameId} label={translate("name")}>
          <input
            id={nameId}
            required
            value={name}
            onChange={(event) => setName(event.target.value)}
            className={`${FIELD_CLASS} w-64`}
          />
        </Field>
        <div className="w-48">
          <Field id={platformId} label={translate("platform")}>
            <Combobox
              id={platformId}
              value={platform}
              onValueChange={setPlatform}
              options={platformOptions}
              searchPlaceholder={translate("platformSearch")}
              emptyText={translate("platformEmpty")}
            />
          </Field>
        </div>
        <button
          type="submit"
          disabled={createProject.isPending || name.trim() === ""}
          className={PRIMARY_BUTTON_CLASS}
        >
          {createProject.isPending ? translate("pending") : translate("submit")}
        </button>
      </div>

      {errorMessage ? (
        <p role="alert" className="mt-2 text-sm text-error">
          {errorMessage}
        </p>
      ) : null}
    </form>
  );
}
