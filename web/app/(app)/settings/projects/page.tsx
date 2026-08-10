import { redirect } from "next/navigation";

// Projects moved out of Settings to the top-level Projects section (#124). Keep this route as a
// redirect so old links and bookmarks land on the new home.
export default function ProjectsSettingsPage() {
  redirect("/projects");
}
