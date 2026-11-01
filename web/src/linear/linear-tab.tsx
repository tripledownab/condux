"use client";

import { usePathname } from "next/navigation";
import { useTranslations } from "next-intl";
import { useEffect, useState } from "react";
import {
  useDisconnectLinear,
  useGetLinearConnection,
  useGetLinearProject,
  useLinearConnect,
} from "@/src/api/generated/condux";
import { PRIMARY_BUTTON_CLASS, SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { Notice } from "@/src/components/notice";
import { readSearchParam, removeSearchParams } from "@/src/lib/url-params";
import { ProjectTab } from "@/src/projects/project-tab";
import { LinearTeamChoice } from "./linear-team-choice";

// How a connect attempt came back through Linear: the callback puts it in ?linear=.
enum ConnectOutcome {
  Connected = "connected",
  Declined = "declined",
  Failed = "failed",
}

// A project's Linear tab (ADR-0053): connect the org's workspace (once, from whichever project goes first),
// choose this project's team, and disconnect. The connection is the org's, so disconnecting names every
// project that loses its team before it goes. Admin+ changes anything; members see where tickets land.
export function LinearTab({
  projectId,
  orgId,
  canManage,
}: {
  projectId: number;
  orgId: number;
  canManage: boolean;
}) {
  const translate = useTranslations("linear");
  const pathname = usePathname();
  const status = useGetLinearProject(projectId);
  const connect = useLinearConnect();
  const [outcome, setOutcome] = useState<ConnectOutcome | null>(null);
  // Kept here rather than in Disconnect, which unmounts the moment the connection is gone.
  const [revokeFailed, setRevokeFailed] = useState(false);
  useEffect(() => {
    const returned = readSearchParam("linear");
    if (Object.values(ConnectOutcome).includes(returned as ConnectOutcome)) {
      setOutcome(returned as ConnectOutcome);
      removeSearchParams("linear");
    }
  }, []);

  if (status.isPending) {
    return <Notice>{translate("loading")}</Notice>;
  }
  const data = status.data?.status === 200 ? status.data.data : null;
  if (data === null) {
    return <Notice>{translate("loadFailed")}</Notice>;
  }

  const start = () =>
    connect.mutate(
      { orgId, data: { returnPath: `${pathname}?tab=${ProjectTab.Linear}` } },
      {
        onSuccess: (response) => {
          if (response.status === 200) {
            window.location.href = response.data.authorizeUrl;
          }
        },
      },
    );
  const needsConnect = !data.connected || data.broken;

  return (
    <div className="flex flex-col gap-4 rounded-lg border border-border bg-card p-4">
      <p className="text-sm text-muted-foreground">
        {!data.connected
          ? translate("connectPrompt")
          : data.broken
            ? translate("broken", { workspace: data.workspaceName ?? "" })
            : translate("connected", { workspace: data.workspaceName ?? "" })}
      </p>
      {needsConnect && canManage ? (
        <div className="flex flex-col items-start gap-2">
          <button
            type="button"
            onClick={start}
            disabled={connect.isPending}
            className={PRIMARY_BUTTON_CLASS}
          >
            {connect.isPending
              ? translate("connecting")
              : data.broken
                ? translate("reconnect")
                : translate("connect")}
          </button>
          <p className="text-xs text-muted-foreground">{translate("workspaceAdminNeeded")}</p>
        </div>
      ) : null}
      {needsConnect && !canManage ? (
        <p className="text-xs text-muted-foreground">{translate("adminOnly")}</p>
      ) : null}
      {connect.isError ? <p className="text-sm text-error">{translate("connectFailed")}</p> : null}
      {outcome === ConnectOutcome.Failed ? (
        <p className="text-sm text-error">{translate("outcomeFailed")}</p>
      ) : null}
      {outcome === ConnectOutcome.Declined ? (
        <p className="text-sm text-muted-foreground">{translate("outcomeDeclined")}</p>
      ) : null}
      {data.connected && !data.broken ? (
        <LinearTeamChoice
          projectId={projectId}
          mapping={data.mapping ?? null}
          canManage={canManage}
        />
      ) : null}
      {data.connected && canManage ? (
        <Disconnect
          orgId={orgId}
          onDone={(revoked) => {
            setRevokeFailed(!revoked);
            status.refetch();
          }}
        />
      ) : null}
      {revokeFailed ? <p className="text-sm text-error">{translate("revokeFailed")}</p> : null}
    </div>
  );
}

// Disconnecting is the org's, not the project's, so it says which projects lose their team first, and
// says so again if Linear could not be told and the grant may still be live there.
function Disconnect({ orgId, onDone }: { orgId: number; onDone: (revoked: boolean) => void }) {
  const translate = useTranslations("linear");
  const [confirming, setConfirming] = useState(false);
  const org = useGetLinearConnection(orgId, { query: { enabled: confirming } });
  const disconnect = useDisconnectLinear();
  const mapped = org.data?.status === 200 ? org.data.data.mappedProjects : [];

  if (!confirming) {
    return (
      <div>
        <button
          type="button"
          onClick={() => setConfirming(true)}
          className={SECONDARY_BUTTON_CLASS}
        >
          {translate("disconnect")}
        </button>
      </div>
    );
  }
  return (
    <div className="flex flex-col items-start gap-2 rounded-md border border-border bg-background p-3">
      <p className="text-sm text-muted-foreground">{translate("disconnectConfirm")}</p>
      {mapped.length > 0 ? (
        <ul className="list-disc pl-5 text-sm">
          {mapped.map((name) => (
            <li key={name}>{name}</li>
          ))}
        </ul>
      ) : null}
      <div className="flex flex-wrap items-center gap-2">
        <button
          type="button"
          disabled={disconnect.isPending}
          className={PRIMARY_BUTTON_CLASS}
          onClick={() =>
            disconnect.mutate(
              { orgId },
              {
                onSuccess: (response) => {
                  setConfirming(false);
                  onDone(response.status !== 200 || response.data.revoked);
                },
              },
            )
          }
        >
          {disconnect.isPending ? translate("disconnecting") : translate("disconnect")}
        </button>
        <button
          type="button"
          onClick={() => setConfirming(false)}
          className={SECONDARY_BUTTON_CLASS}
        >
          {translate("cancel")}
        </button>
      </div>
      {disconnect.isError ? (
        <p className="text-sm text-error">{translate("disconnectFailed")}</p>
      ) : null}
    </div>
  );
}
