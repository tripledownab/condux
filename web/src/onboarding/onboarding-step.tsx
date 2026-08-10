import { RiCheckLine } from "@remixicon/react";
import type { ReactNode } from "react";

// A step's state drives its look: a done step shows a check, the active step shows its action, a locked
// step is dimmed until the previous ones are done.
export enum StepStatus {
  Done = "done",
  Active = "active",
  Locked = "locked",
}

// One numbered onboarding step: a status badge, a title + description, and (only while active) its
// action. Presentational — the parent decides each step's status from existing data.
export function OnboardingStep({
  index,
  status,
  title,
  description,
  children,
}: {
  index: number;
  status: StepStatus;
  title: string;
  description: string;
  children?: ReactNode;
}) {
  return (
    <li
      className={`rounded-lg border border-border bg-card p-4 ${status === StepStatus.Locked ? "opacity-60" : ""}`}
    >
      <div className="flex items-start gap-3">
        <span
          className={`flex size-7 shrink-0 items-center justify-center rounded-full text-xs font-medium ${
            status === StepStatus.Done
              ? "bg-info text-background"
              : "border border-border text-muted-foreground"
          }`}
        >
          {status === StepStatus.Done ? <RiCheckLine className="size-4" /> : index}
        </span>
        <div className="flex-1">
          <h3 className="font-heading text-sm font-semibold text-foreground">{title}</h3>
          <p className="mt-0.5 text-xs text-muted-foreground">{description}</p>
          {/* Children show while the step is workable — active, or done with an optional extra
              (the org step keeps its invite form after completion). */}
          {status !== StepStatus.Locked && children ? <div className="mt-3">{children}</div> : null}
        </div>
      </div>
    </li>
  );
}
