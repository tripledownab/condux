import { AppearanceSettings } from "@/src/settings/appearance-settings";
import { OrgGeneral } from "@/src/settings/org-general";

export default function GeneralSettingsPage() {
  return (
    <div className="flex flex-col gap-8">
      <OrgGeneral />
      <AppearanceSettings />
    </div>
  );
}
