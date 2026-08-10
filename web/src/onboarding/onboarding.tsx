"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import type { ReactNode } from "react";
import { getMeQueryKey, useCompleteOnboarding } from "@/src/api/generated/condux";
import { PRIMARY_BUTTON_CLASS } from "@/src/components/form";
import { Notice } from "@/src/components/notice";
import { ProjectStatus, useCurrentProject } from "@/src/issues/current-project";
import { ROUTES } from "@/src/routes";
import { CreateProjectForm } from "@/src/settings/create-project-form";
import { CreateOrgForm } from "./create-org-form";
import { InviteTeammates } from "./invite-teammates";
import { OnboardingStep, StepStatus } from "./onboarding-step";
import { EventStep, RepoStep } from "./steps";

// The guided first-run checklist: organization → first project → connect a repo → send the first event.
// Each step's status is derived from existing data (no onboarding state is stored), so the checklist
// reflects reality and completes as the user actually does each step. Signup mints no org (ADR-0018),
// so step 1 starts active with the create-org form (and, once created, the optional teammate invites).
export function Onboarding() {
  const translate = useTranslations("onboarding");
  const current = useCurrentProject();

  if (current.status === ProjectStatus.Loading) {
    return (
      <Shell>
        <Notice>{translate("loading")}</Notice>
      </Shell>
    );
  }
  if (current.status === ProjectStatus.Error) {
    return (
      <Shell>
        <Notice>{translate("error")}</Notice>
      </Shell>
    );
  }
  if (current.status === ProjectStatus.NoOrg) {
    return (
      <Shell>
        <ol className="flex flex-col gap-3">
          <OnboardingStep
            index={1}
            status={StepStatus.Active}
            title={translate("org.title")}
            description={translate("org.createDescription")}
          >
            <CreateOrgForm />
          </OnboardingStep>
          <OnboardingStep
            index={2}
            status={StepStatus.Locked}
            title={translate("project.title")}
            description={translate("project.description")}
          />
          <OnboardingStep
            index={3}
            status={StepStatus.Locked}
            title={translate("repo.title")}
            description={translate("repo.description")}
          />
          <OnboardingStep
            index={4}
            status={StepStatus.Locked}
            title={translate("event.title")}
            description={translate("event.description")}
          />
        </ol>
      </Shell>
    );
  }

  const org = current.org;
  const project = current.status === ProjectStatus.Ready ? current.project : null;

  return (
    <Shell>
      <ol className="flex flex-col gap-3">
        <OnboardingStep
          index={1}
          status={StepStatus.Done}
          title={translate("org.title")}
          description={translate("org.description", { org: org.name })}
        >
          <InviteTeammates orgId={org.id} />
        </OnboardingStep>
        <OnboardingStep
          index={2}
          status={project ? StepStatus.Done : StepStatus.Active}
          title={translate("project.title")}
          description={translate("project.description")}
        >
          <CreateProjectForm orgId={org.id} />
        </OnboardingStep>
        {project ? (
          <>
            <RepoStep projectId={project.id} projectName={project.name} />
            <EventStep projectId={project.id} publicId={project.publicId} />
          </>
        ) : (
          <>
            <OnboardingStep
              index={3}
              status={StepStatus.Locked}
              title={translate("repo.title")}
              description={translate("locked")}
            />
            <OnboardingStep
              index={4}
              status={StepStatus.Locked}
              title={translate("event.title")}
              description={translate("locked")}
            />
          </>
        )}
      </ol>
      <FinishBar ready={project !== null} />
    </Shell>
  );
}

// Ends onboarding and reveals the full app. Connecting a repo and sending the first event are optional, so
// Finish only requires the mandatory steps (org, already done here, + a project). It records completion
// server-side (users.onboarded_at), refreshes /me so the gate opens, then lands on the dashboard.
function FinishBar({ ready }: { ready: boolean }) {
  const translate = useTranslations("onboarding");
  const router = useRouter();
  const queryClient = useQueryClient();
  const complete = useCompleteOnboarding();

  const finish = () =>
    complete.mutate(undefined, {
      onSuccess: async () => {
        await queryClient.invalidateQueries({ queryKey: getMeQueryKey() });
        router.push(ROUTES.home);
      },
    });

  return (
    <div className="mt-8 flex flex-wrap items-center gap-3 border-t border-border pt-6">
      <button
        type="button"
        onClick={finish}
        disabled={!ready || complete.isPending}
        className={PRIMARY_BUTTON_CLASS}
      >
        {complete.isPending ? translate("finishing") : translate("finish")}
      </button>
      <p className="text-xs text-muted-foreground">
        {ready ? translate("finishReady") : translate("finishHint")}
      </p>
    </div>
  );
}

function Shell({ children }: { children: ReactNode }) {
  const translate = useTranslations("onboarding");
  return (
    <div>
      <h1 className="font-heading text-2xl font-semibold text-foreground">{translate("title")}</h1>
      <p className="mt-1 text-sm text-muted-foreground">{translate("subtitle")}</p>
      <div className="mt-6">{children}</div>
    </div>
  );
}
