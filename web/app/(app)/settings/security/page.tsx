import { MfaSettings } from "@/src/settings/mfa-settings";
import { PasswordSettings } from "@/src/settings/password-settings";

export default function SecuritySettingsPage() {
  return (
    <div className="flex flex-col gap-6">
      <PasswordSettings />
      <MfaSettings />
    </div>
  );
}
