"use client";

import Link from "next/link";
import { useTranslations } from "next-intl";
import { Dialog as DialogPrimitive } from "radix-ui";
import { useState } from "react";
import { useAiFixUsage, useListGithubBranches, useListRepos } from "@/src/api/generated/condux";
import { PRIMARY_BUTTON_CLASS, SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { Combobox } from "@/src/components/ui/combobox";
import { formatUsd } from "@/src/lib/format";
import { daysSince } from "@/src/lib/time";
import { ROUTES } from "@/src/routes";

// Confirms a Conductor run before it starts (#128). A run costs a real AI-fix allowance (and money), so
// this guards against an accidental click and shows how many fixes remain this period. For an issue fix
// (a projectId is given) it is also where the run's target is made explicit: which linked repo and which
// base branch the draft PR opens against (a project may link several repos, and any branch is fair game —
// the branch is a per-run choice, never stored), both defaulting to the sole/first repo and its default
// branch. Omit projectId (e.g. a CVE bump, whose repo is fixed) for an allowance-only confirmation. Only
// fires on Confirm; onConfirm receives (repoId, baseBranch), both null when no target was chosen.
// How quiet an issue has to go before the dialog says so. A week is long enough that a deploy has
// probably shipped since, and short enough to still catch the case worth catching.
const STALE_AFTER_DAYS = 7;

export function SuggestFixDialog({
  open,
  onOpenChange,
  orgId,
  projectId,
  lastSeen,
  pending,
  onConfirm,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  orgId: number;
  projectId?: number;
  /** When the issue was last seen, for the stale warning. Omitted by callers with no issue (a CVE bump). */
  lastSeen?: string;
  pending: boolean;
  onConfirm: (repoId: string | null, baseBranch: string | null) => void;
}) {
  const translate = useTranslations("issues.fix.confirm");
  const tCommon = useTranslations("common");
  const usage = useAiFixUsage(orgId, { query: { enabled: open } });
  const data = usage.data?.data;
  // Out of allowance: confirming would fire a request the control plane is certain to reject with
  // ai_fix_quota_exceeded, so offer the plan instead of a guaranteed error. Undefined while the usage
  // query is still loading, which must not read as exhausted and disable the button.
  const exhausted = data?.uncappedFixes === false && data.remainingFixes === 0;
  // The fair-use compute ceiling is checked BEFORE the allowance server-side, so an org can still have
  // runs left and be refused. Mirrors AiFixBudget.IsOverCap (null never blocks, spend >= cap does).
  // Pre-emptive only: the 409 stays the authority, so drift here degrades to a correct error on click
  // rather than a wrong outcome. Shown ahead of the count for the same reason the server checks it first.
  const capReached =
    data !== undefined && data.capUsd !== null && data.monthToDateUsd >= data.capUsd;

  // Quiet for this long and the issue may already have been fixed by a later commit, which is exactly
  // when a run gets spent re-fixing something.
  const quietDays = lastSeen === undefined ? 0 : daysSince(lastSeen);
  const staleDays = quietDays >= STALE_AFTER_DAYS ? quietDays : null;

  const repos = useListRepos(projectId ?? 0, {
    query: { enabled: open && projectId !== undefined },
  });
  const linked = repos.data?.data ?? [];

  // Target derived so changing the repo re-defaults the branch without an effect: repoId/branch hold the
  // user's overrides, everything else falls back to the sole/first repo and its default branch.
  const [repoId, setRepoId] = useState<string | null>(null);
  const [branch, setBranch] = useState<string | null>(null);
  const selectedRepo = linked.find((repo) => repo.id === repoId) ?? linked[0];
  const defaultBranch = selectedRepo?.defaultBranch ?? "";

  const branches = useListGithubBranches(
    orgId,
    { repo: selectedRepo?.repoFullName ?? "" },
    { query: { enabled: open && Boolean(selectedRepo) } },
  );
  const liveBranches = branches.data?.status === 200 ? branches.data.data.branches : [];
  // Keep the default branch selectable even when the live list misses it (GitHub off/error, or a branch
  // that no longer exists), mirroring the settings branch picker.
  const branchOptions =
    defaultBranch && !liveBranches.includes(defaultBranch)
      ? [defaultBranch, ...liveBranches]
      : liveBranches;
  const selectedBranch = branch ?? defaultBranch;
  const repoOptions = linked.map((repo) => ({ value: repo.id, label: repo.repoFullName }));
  const branchOptionItems = branchOptions.map((name) => ({ value: name, label: name }));

  return (
    <DialogPrimitive.Root open={open} onOpenChange={onOpenChange}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className="fixed inset-0 z-50 bg-black/50" />
        <DialogPrimitive.Content className="fixed left-1/2 top-1/2 z-50 w-[90vw] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-lg border border-border bg-card p-6 shadow-lg focus:outline-none">
          <DialogPrimitive.Title className="font-heading text-lg font-semibold text-foreground">
            {translate("title")}
          </DialogPrimitive.Title>
          <DialogPrimitive.Description className="mt-2 text-sm text-muted-foreground">
            {translate("body")}
          </DialogPrimitive.Description>

          {selectedRepo ? (
            <div className="mt-4 flex flex-col gap-3">
              <div className="flex flex-col gap-1 text-sm">
                <span className="text-muted-foreground">{translate("repoLabel")}</span>
                {linked.length > 1 ? (
                  <Combobox
                    value={selectedRepo.id}
                    onValueChange={(id) => {
                      setRepoId(id);
                      setBranch(null);
                    }}
                    options={repoOptions}
                    aria-label={translate("repoLabel")}
                    searchPlaceholder={tCommon("comboboxSearch")}
                    emptyText={tCommon("comboboxEmpty")}
                  />
                ) : (
                  <span className="font-medium text-foreground">{selectedRepo.repoFullName}</span>
                )}
              </div>

              <div className="flex flex-col gap-1 text-sm">
                <span className="text-muted-foreground">{translate("branchLabel")}</span>
                {branchOptions.length > 1 ? (
                  <Combobox
                    value={selectedBranch}
                    onValueChange={setBranch}
                    options={branchOptionItems}
                    aria-label={translate("branchLabel")}
                    searchPlaceholder={tCommon("comboboxSearch")}
                    emptyText={tCommon("comboboxEmpty")}
                  />
                ) : (
                  <span className="font-medium text-foreground">{selectedBranch}</span>
                )}
              </div>
            </div>
          ) : null}

          {/* A warning, not a block: an issue can be worth fixing long after it stopped firing, and only
              the person reading it knows. What they cannot know from this dialog is that it went quiet,
              which is how a run gets spent re-fixing something a later commit already fixed. */}
          {staleDays !== null ? (
            <p className="mt-3 text-sm text-muted-foreground">
              {translate("stale", { days: staleDays })}
            </p>
          ) : null}

          {capReached ? (
            <p className="mt-3 text-sm text-foreground">
              {translate("capReached", {
                spent: formatUsd(data.monthToDateUsd),
                cap: formatUsd(data.capUsd ?? 0),
              })}{" "}
              <Link
                href={ROUTES.settingsGeneral}
                className="text-foreground underline underline-offset-4"
              >
                {translate("exhaustedCta")}
              </Link>
            </p>
          ) : data?.uncappedFixes ? (
            <p className="mt-3 text-sm text-foreground">{translate("remainingUnlimited")}</p>
          ) : exhausted ? (
            <p className="mt-3 text-sm text-foreground">
              {translate("exhausted")}{" "}
              <Link
                href={ROUTES.settingsGeneral}
                className="text-foreground underline underline-offset-4"
              >
                {translate("exhaustedCta")}
              </Link>
            </p>
          ) : typeof data?.remainingFixes === "number" ? (
            <p className="mt-3 text-sm text-foreground tabular-nums">
              {translate("remaining", { count: data.remainingFixes })}
            </p>
          ) : null}

          <div className="mt-5 flex justify-end gap-2">
            <DialogPrimitive.Close className={SECONDARY_BUTTON_CLASS}>
              {translate("cancel")}
            </DialogPrimitive.Close>
            <button
              type="button"
              onClick={() => onConfirm(selectedRepo?.id ?? null, selectedBranch || null)}
              disabled={pending || exhausted || capReached}
              className={PRIMARY_BUTTON_CLASS}
            >
              {translate("confirm")}
            </button>
          </div>
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  );
}
