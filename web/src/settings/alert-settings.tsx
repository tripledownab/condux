"use client";

import { useTranslations } from "next-intl";
import { AlertRules } from "@/src/alerts/alert-rules";
import { Notice } from "@/src/components/notice";
import { ProjectStatus, useCurrentProject } from "@/src/issues/current-project";

// The Alerts settings tab: the current project's alert rules, or a notice when the project cannot be
// resolved yet (alerts are per project, following the ProjectSwitcher).
export function AlertSettings() {
  const translate = useTranslations("settings");
  const current = useCurrentProject();

  if (current.status === ProjectStatus.Loading) {
    return <Notice>{translate("loading")}</Notice>;
  }
  if (current.status === ProjectStatus.Error) {
    return <Notice>{translate("error")}</Notice>;
  }
  if (current.status === ProjectStatus.NoOrg) {
    return <Notice>{translate("noOrg")}</Notice>;
  }
  if (current.status === ProjectStatus.NoProjects) {
    return <Notice>{translate("alerts.noProject")}</Notice>;
  }

  return <AlertRules projectId={current.project.id} />;
}
