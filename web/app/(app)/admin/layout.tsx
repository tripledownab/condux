import { getTranslations } from "next-intl/server";
import type { ReactNode } from "react";
import { AdminGuard } from "@/src/admin/admin-guard";
import { AdminTabs } from "@/src/admin/admin-tabs";
import { PageContainer } from "@/src/components/page-container";

// The platform admin shell: a title and tab bar (Overview / Organizations / Users) above every admin
// page, all behind AdminGuard so only platform admins reach it. Mirrors the settings layout.
export default async function AdminLayout({ children }: { children: ReactNode }) {
  const translate = await getTranslations("admin");
  return (
    <AdminGuard>
      <PageContainer>
        <h1 className="font-heading text-2xl font-semibold text-foreground">
          {translate("title")}
        </h1>
        <div className="mt-4">
          <AdminTabs />
        </div>
        <div className="mt-6">{children}</div>
      </PageContainer>
    </AdminGuard>
  );
}
