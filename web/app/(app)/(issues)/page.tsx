import { getTranslations } from "next-intl/server";

// Home inside the issues surface: no issue selected yet, so the main pane prompts for one. The
// working content lives in the rails beside this.
export default async function DashboardHomePage() {
  const translate = await getTranslations("issues");
  return (
    <div className="flex h-full items-center justify-center p-6">
      <p className="max-w-sm text-center text-sm text-muted-foreground">
        {translate("selectIssue")}
      </p>
    </div>
  );
}
