import { getTranslations } from "next-intl/server";

// Landing inside the fixes surface: no fix selected yet, so the main pane prompts for one.
export default async function FixesHomePage() {
  const translate = await getTranslations("fixes");
  return (
    <div className="flex h-full items-center justify-center p-6">
      <p className="max-w-sm text-center text-sm text-muted-foreground">{translate("selectFix")}</p>
    </div>
  );
}
