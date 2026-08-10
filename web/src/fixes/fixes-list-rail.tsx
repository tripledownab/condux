"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { useTranslations } from "next-intl";
import type { FixListItem } from "@/src/api/generated/model";
import { fixStatusMeta, verifyStatusMeta } from "@/src/issues/fix-format";
import { formatRelativeTime } from "@/src/lib/time";
import { fixDetailPath } from "@/src/routes";

// The second rail of the Fixes surface: compact rows for the filtered fixes, worked through while the
// detail stays open in the main pane. The active fix is derived from the route; an unviewed fix shows a
// dot. The row's fix status + verification badges tell the lifecycle at a glance.
export function FixesListRail({
  heading,
  isPending,
  isError,
  fixes,
}: {
  heading: string;
  isPending: boolean;
  isError: boolean;
  fixes: FixListItem[];
}) {
  const translate = useTranslations("fixes");
  const translateStatus = useTranslations("issues.fix.status");
  const translateVerify = useTranslations("issues.fix.verify");
  const params = useParams<{ fixId?: string }>();
  const activeFixId = params?.fixId;

  return (
    // Only the list scrolls; the heading stays put. Scrolling the whole aside carried it away with the
    // list, so you lost track of which view you were in as soon as you moved down.
    <aside className="flex h-full flex-col overflow-hidden bg-card/30">
      <div className="flex h-10 shrink-0 items-center border-b border-border px-3 text-xs text-muted-foreground">
        {heading}
      </div>
      {/* min-h-0 is load-bearing: a flex child defaults to min-height:auto, which refuses to shrink
          below its content, so without it the list grows the rail instead of scrolling inside it. */}
      <div className="min-h-0 flex-1 overflow-y-auto">
        {isPending ? <RailNotice text={translate("loading")} /> : null}
        {isError ? <RailNotice text={translate("error")} /> : null}
        {!isPending && !isError && fixes.length === 0 ? (
          <RailNotice text={translate("empty")} />
        ) : null}
        <ul>
          {fixes.map((fix) => {
            const active = fix.id === activeFixId;
            const statusMeta = fixStatusMeta(fix.status);
            const verifyMeta = verifyStatusMeta(fix.verifyStatus);
            return (
              <li key={fix.id} className="border-b border-border/50">
                <Link
                  href={fixDetailPath(fix.id)}
                  aria-current={active ? "page" : undefined}
                  className={`block px-3 py-2.5 ${active ? "bg-secondary" : "hover:bg-secondary/40"}`}
                >
                  <div className="flex items-center gap-2 text-xs">
                    {!fix.viewed ? (
                      <>
                        <span aria-hidden className="size-1.5 shrink-0 rounded-full bg-primary" />
                        <span className="sr-only">{translate("unviewed")}</span>
                      </>
                    ) : null}
                    {/* Once merged the verification state is the true status; "Draft PR ready" would
                      be stale, so the verify badge replaces the run status rather than joining it. */}
                    {verifyMeta ? (
                      <span className={`uppercase ${verifyMeta.className}`}>
                        {translateVerify(verifyMeta.key)}
                      </span>
                    ) : (
                      <span className={`uppercase ${statusMeta.className}`}>
                        {translateStatus(statusMeta.key)}
                      </span>
                    )}
                    <span className="ml-auto text-muted-foreground">
                      {formatRelativeTime(fix.createdAt)}
                    </span>
                  </div>
                  <div className="mt-0.5 truncate text-xs font-medium text-foreground">
                    {fix.issueTitle}
                  </div>
                  <div className="truncate text-xs text-muted-foreground">{fix.repoFullName}</div>
                </Link>
              </li>
            );
          })}
        </ul>
      </div>
    </aside>
  );
}

function RailNotice({ text }: { text: string }) {
  return <p className="px-3 py-3 text-xs text-muted-foreground">{text}</p>;
}
