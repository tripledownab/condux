"use client";

import { useTranslations } from "next-intl";
import { FixKind, fixStatusMeta, verifyStatusMeta } from "@/src/issues/fix-format";

// A Conductor run's status as a coloured label. Once a draft PR merges, its verification state is the
// true status and "Draft PR ready" would be stale, so the verification label replaces the run status
// rather than joining it. A CVE bump's verification (ADR-0051) is worded for what it watches.
export function FixStatusLabel({
  status,
  verifyStatus,
  kind = FixKind.IssueFix,
  className = "",
}: {
  status: number;
  verifyStatus?: number;
  kind?: FixKind;
  className?: string;
}) {
  const translateStatus = useTranslations("issues.fix.status");
  const translateVerify = useTranslations("issues.fix.verify");
  const verifyMeta = verifyStatusMeta(verifyStatus, kind);
  if (verifyMeta !== null) {
    return (
      <span className={`${className} ${verifyMeta.className}`}>
        {translateVerify(verifyMeta.key)}
      </span>
    );
  }
  const meta = fixStatusMeta(status);
  return <span className={`${className} ${meta.className}`}>{translateStatus(meta.key)}</span>;
}
