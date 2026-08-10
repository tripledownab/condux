"use client";

import Link from "next/link";
import { useTranslations } from "next-intl";
import { useState } from "react";
import { useListKeys, useListProjects } from "@/src/api/generated/condux";
import type { ProjectRecord } from "@/src/api/generated/model";
import { Field } from "@/src/components/form";
import { Notice } from "@/src/components/notice";
import { Combobox } from "@/src/components/ui/combobox";
import { buildDsn } from "@/src/lib/dsn";
import { OrgStatus, useCurrentOrg } from "@/src/orgs/current-org";
import { useSelectedProject } from "@/src/projects/selected-project";
import { projectDetailPath, ROUTES } from "@/src/routes";
import { CodeBlock } from "./code-block";
import { defaultPlatform, PLATFORMS, type Snippet } from "./platforms";

// The in-app SDK setup guide: pick a project + one of its DSN keys, choose a platform, and copy the
// prefilled snippets. Scoped to the current org; the selectors let a developer target any project/key.
export function DeveloperGuide() {
  const translate = useTranslations("developer");
  const current = useCurrentOrg();

  if (current.status === OrgStatus.Loading) {
    return <Notice>{translate("loading")}</Notice>;
  }
  if (current.status === OrgStatus.Error) {
    return <Notice>{translate("error")}</Notice>;
  }
  if (current.status === OrgStatus.NoOrg) {
    return <Notice>{translate("empty")}</Notice>;
  }

  return (
    <div className="flex flex-col gap-8">
      <div>
        <h1 className="font-heading text-2xl font-semibold text-foreground">
          {translate("title")}
        </h1>
        <p className="mt-1 text-sm text-muted-foreground">{translate("description")}</p>
      </div>
      <PickProject orgId={current.org.id} />
    </div>
  );
}

function PickProject({ orgId }: { orgId: number }) {
  const translate = useTranslations("developer");
  const { selectedProjectId } = useSelectedProject();
  const projects = useListProjects(orgId);
  const [projectId, setProjectId] = useState<number | null>(null);

  if (projects.isPending) {
    return <Notice>{translate("loading")}</Notice>;
  }
  if (projects.isError) {
    return <Notice>{translate("error")}</Notice>;
  }
  const list = projects.data?.data ?? [];
  if (list.length === 0) {
    return (
      <Notice>
        {translate("empty")}{" "}
        <Link href={ROUTES.projects} className="text-primary hover:underline">
          {translate("emptyLink")}
        </Link>
      </Notice>
    );
  }
  const project = list.find((entry) => entry.id === (projectId ?? selectedProjectId)) ?? list[0];

  return (
    <ProjectSetup key={project.id} projects={list} project={project} onProject={setProjectId} />
  );
}

function ProjectSetup({
  projects,
  project,
  onProject,
}: {
  projects: ProjectRecord[];
  project: ProjectRecord;
  onProject: (id: number) => void;
}) {
  const translate = useTranslations("developer");
  const [platformId, setPlatformId] = useState(() => defaultPlatform(project.platform).id);
  const [keyId, setKeyId] = useState<number | null>(null);
  const keys = useListKeys(project.id);
  const activeKeys = (keys.data?.data ?? []).filter((key) => key.isActive);
  const activeKey = activeKeys.find((key) => key.id === keyId) ?? activeKeys[0];
  const dsn = activeKey ? buildDsn(activeKey.publicKey, project.publicId) : null;
  const platform = PLATFORMS.find((entry) => entry.id === platformId) ?? PLATFORMS[0];

  const snippetTitle = (snippet: Snippet): string =>
    snippet.titleKey === "framework"
      ? translate("snippets.framework", { framework: snippet.framework ?? "" })
      : translate(`snippets.${snippet.titleKey}`);

  return (
    <div className="flex flex-col gap-8">
      <div className="grid gap-4 sm:grid-cols-2">
        <Field id="developer-project" label={translate("projectLabel")}>
          <Combobox
            id="developer-project"
            value={String(project.id)}
            onValueChange={(value) => onProject(Number(value))}
            options={projects.map((entry) => ({ value: String(entry.id), label: entry.name }))}
            searchPlaceholder={translate("search")}
            emptyText={translate("noResults")}
          />
        </Field>
        {activeKeys.length > 0 ? (
          <Field id="developer-key" label={translate("dsn.keyLabel")}>
            <Combobox
              id="developer-key"
              value={String(activeKey?.id)}
              onValueChange={(value) => setKeyId(Number(value))}
              options={activeKeys.map((key) => ({
                value: String(key.id),
                label: key.label?.trim() ? key.label : key.publicKey,
              }))}
              searchPlaceholder={translate("search")}
              emptyText={translate("noResults")}
            />
          </Field>
        ) : null}
      </div>

      <section className="flex flex-col gap-3">
        <h2 className="font-heading text-lg font-semibold text-foreground">
          {translate("dsn.title")}
        </h2>
        {dsn ? (
          <CodeBlock title="DSN" code={dsn} />
        ) : (
          <p className="text-sm text-muted-foreground">
            {translate("dsn.noKey")}{" "}
            <Link
              href={projectDetailPath(project.publicId)}
              className="text-primary hover:underline"
            >
              {translate("dsn.noKeyLink")}
            </Link>
          </p>
        )}
      </section>

      <Field id="developer-platform" label={translate("choosePlatform")}>
        <Combobox
          id="developer-platform"
          aria-label={translate("choosePlatform")}
          value={platform.id}
          onValueChange={setPlatformId}
          options={PLATFORMS.map((entry) => ({ value: entry.id, label: entry.name }))}
          searchPlaceholder={translate("search")}
          emptyText={translate("noResults")}
          className="sm:w-80"
        />
      </Field>

      <section className="flex flex-col gap-3">
        {platform.snippets.map((snippet) => (
          <CodeBlock
            key={`${platform.id}-${snippet.titleKey}`}
            title={snippetTitle(snippet)}
            language={snippet.language}
            code={snippet.code(dsn ?? "<your-dsn>")}
          />
        ))}
      </section>
    </div>
  );
}
