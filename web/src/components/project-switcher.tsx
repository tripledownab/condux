"use client";

import { RiArrowDownSLine, RiStackLine } from "@remixicon/react";
import { useTranslations } from "next-intl";
import type { ReactNode } from "react";
import { useListProjects } from "@/src/api/generated/condux";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/src/components/ui/dropdown-menu";
import { ProjectStatus, useCurrentProject } from "@/src/issues/current-project";
import { useSelectedProject } from "@/src/projects/selected-project";

// Switches the project the dashboard is scoped to: a Radix dropdown listing the current org's projects,
// persisting the choice (useSelectedProject). Lives in the sidebar base above the account card — as a
// full-width card when expanded, an icon button when collapsed (compact). Rendered only once there is an
// org with a project; the settings surfaces handle the no-org / no-projects cases.
export function ProjectSwitcher({ compact = false }: { compact?: boolean }) {
  const translate = useTranslations("project");
  const current = useCurrentProject();
  const { selectProject } = useSelectedProject();
  const orgId = current.status === ProjectStatus.Ready ? current.org.id : 0;
  const projects = useListProjects(orgId, { query: { enabled: orgId !== 0 } });

  if (current.status === ProjectStatus.Loading) {
    return compact ? null : <Label>{translate("loading")}</Label>;
  }
  if (current.status !== ProjectStatus.Ready) {
    return null;
  }

  const list = projects.data?.data ?? [current.project];
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        {compact ? (
          <button
            type="button"
            aria-label={translate("switch")}
            title={current.project.name}
            className="flex size-8 items-center justify-center rounded-md border border-border bg-background text-foreground transition-colors hover:bg-secondary"
          >
            <RiStackLine className="size-4" aria-hidden="true" />
          </button>
        ) : (
          <button
            type="button"
            aria-label={translate("switch")}
            title={current.project.name}
            className="flex w-full items-center gap-2 rounded-md border border-border bg-background p-2 text-left text-xs transition-colors hover:bg-secondary/50"
          >
            <span className="flex size-7 shrink-0 items-center justify-center rounded-md bg-secondary text-foreground">
              <RiStackLine className="size-4" aria-hidden="true" />
            </span>
            <span className="min-w-0 flex-1 truncate text-foreground">{current.project.name}</span>
            <RiArrowDownSLine
              className="size-4 shrink-0 text-muted-foreground"
              aria-hidden="true"
            />
          </button>
        )}
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" side={compact ? "right" : "top"}>
        <DropdownMenuLabel>{translate("label")}</DropdownMenuLabel>
        <DropdownMenuSeparator />
        <DropdownMenuRadioGroup
          value={String(current.project.id)}
          onValueChange={(value) => selectProject(Number(value))}
        >
          {list.map((project) => (
            <DropdownMenuRadioItem key={project.id} value={String(project.id)}>
              {project.name}
            </DropdownMenuRadioItem>
          ))}
        </DropdownMenuRadioGroup>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

function Label({ children }: { children: ReactNode }) {
  return <span className="text-sm text-muted-foreground">{children}</span>;
}
