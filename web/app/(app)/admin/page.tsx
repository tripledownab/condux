import { redirect } from "next/navigation";

// /admin has no content of its own; land on the Overview tab.
export default function AdminPage() {
  redirect("/admin/overview");
}
