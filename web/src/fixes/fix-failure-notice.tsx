"use client";

import Link from "next/link";
import { useTranslations } from "next-intl";
import { ProjectStatus, useCurrentProject } from "@/src/issues/current-project";
import { FailureRemedy, fixFailureMeta } from "@/src/issues/fix-format";
import { projectDetailPath, ROUTES } from "@/src/routes";

// Why a failed fix run failed and what to do about it, from the closed set of reasons the API returns.
// The error text behind it is never shown: it stays in the audit trail for operators.
export function FixFailureNotice({ reason }: { reason: number | null | undefined }) {
  const translate = useTranslations("fixes.detail.failure");
  const current = useCurrentProject();
  const meta = fixFailureMeta(reason);
  if (meta === null) {
    return null;
  }

  const remedyHref =
    meta.remedy === FailureRemedy.AiProvider
      ? ROUTES.settingsProvider
      : meta.remedy === FailureRemedy.ProjectGitHub && current.status === ProjectStatus.Ready
        ? projectDetailPath(current.project.publicId)
        : null;

  return (
    <section
      role="status"
      className="mt-4 rounded-md border border-error/40 bg-error/5 p-3 text-sm text-foreground"
    >
      <h2 className="text-xs font-medium uppercase text-error">{translate("title")}</h2>
      <p className="mt-1">{translate(meta.key)}</p>
      {meta.remedy === FailureRemedy.None ? null : (
        <p className="mt-1 text-muted-foreground">
          {remedyHref === null ? (
            translate("retryFromIssue")
          ) : (
            <Link href={remedyHref} className="underline underline-offset-2">
              {translate(meta.remedy)}
            </Link>
          )}
        </p>
      )}
    </section>
  );
}
