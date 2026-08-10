import { redirect } from "next/navigation";

// /settings has no content of its own; land on the General tab.
export default function SettingsPage() {
  redirect("/settings/general");
}
