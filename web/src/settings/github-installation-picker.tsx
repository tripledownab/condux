"use client";

import { useTranslations } from "next-intl";
import { useGithubSelect, useGithubSelection } from "@/src/api/generated/condux";
import { PRIMARY_BUTTON_CLASS } from "@/src/components/form";

// The connect flow can only end here when the authorizing user reaches more than one installation of the
// app, which GitHub gives us no way to disambiguate. The candidates arrive in a token the callback signed,
// so the accounts are read back from the server rather than parsed out of the URL: the signature is what
// decides which ids are linkable, and the browser never gets a say in that.
export function GitHubInstallationPicker({
  orgId,
  selection,
  onSelected,
}: {
  orgId: number;
  selection: string;
  onSelected: () => void;
}) {
  const translate = useTranslations("settings.github");
  const candidates = useGithubSelection(orgId, { token: selection });
  const select = useGithubSelect();

  const installations =
    candidates.data?.status === 200 ? candidates.data.data.installations : undefined;
  // An expired or already-used token. The flow is simply restartable, so say that rather than dead-end.
  const expired = candidates.isError || installations?.length === 0;

  if (expired) {
    return <p className="text-sm text-error">{translate("selectExpired")}</p>;
  }

  return (
    <div className="flex flex-col items-start gap-2">
      <p className="text-sm text-muted-foreground">{translate("selectPrompt")}</p>
      <div className="flex flex-wrap gap-2">
        {installations?.map((installation) => (
          <button
            key={installation.installationId}
            type="button"
            disabled={select.isPending}
            onClick={() =>
              select.mutate(
                { orgId, data: { selection, installationId: installation.installationId } },
                { onSuccess: onSelected },
              )
            }
            className={PRIMARY_BUTTON_CLASS}
          >
            {installation.accountLogin}
          </button>
        ))}
      </div>
      {select.isError ? <p className="text-sm text-error">{translate("selectFailed")}</p> : null}
    </div>
  );
}
