"use client";

import { addBreadcrumb, setTag, setUser } from "@condux/nextjs";
import { usePathname } from "next/navigation";
import { useEffect } from "react";
import { useMe } from "@/src/api/generated/condux";
import { OrgStatus, useCurrentOrg } from "@/src/orgs/current-org";

// Condux on Condux (#75), the enrichment half: attach who is signed in, which org they act in, and the
// navigation trail to every self-reported dashboard error, through the same SDK API a customer uses.
// Renders nothing; mounted once in the app shell.
export function ConduxEnrichment() {
  const me = useMe().data?.data;
  const current = useCurrentOrg();
  const pathname = usePathname();

  useEffect(() => {
    setUser(me ? { id: String(me.id), email: me.email } : null);
  }, [me]);

  useEffect(() => {
    setTag("org", current.status === OrgStatus.Ready ? current.org.slug : null);
  }, [current]);

  useEffect(() => {
    if (pathname) {
      addBreadcrumb({ message: pathname, category: "navigation" });
    }
  }, [pathname]);

  return null;
}
