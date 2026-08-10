"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import {
  getListKeysQueryKey,
  useCreateKey,
  useListKeys,
  useRevokeKey,
  useUpdateKey,
} from "@/src/api/generated/condux";
import type { DsnKey } from "@/src/api/generated/model";
import { PRIMARY_BUTTON_CLASS, SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { Notice } from "@/src/components/notice";
import { buildDsn } from "@/src/lib/dsn";
import { KeyNameDialog } from "./key-name-dialog";

// DSN keys for a project: list them (with the full DSN reconstructed), mint a new named one, rename or
// revoke one. Creating and renaming both open a name modal so keys are distinguishable (not all "default").
// Every mutation invalidates the list so the view reflects the change.
// projectId (the bigint) keys the DSN-key API; publicId (the UUID) is the DSN's last segment (#126).
export function DsnKeys({ projectId, publicId }: { projectId: number; publicId: string }) {
  const translate = useTranslations("settings.keys");
  const queryClient = useQueryClient();
  const keys = useListKeys(projectId);
  const createKey = useCreateKey();
  const updateKey = useUpdateKey();
  const revokeKey = useRevokeKey();

  const [createOpen, setCreateOpen] = useState(false);
  const [renaming, setRenaming] = useState<DsnKey | null>(null);

  const invalidate = () =>
    queryClient.invalidateQueries({ queryKey: getListKeysQueryKey(projectId) });

  const create = (label: string) =>
    createKey.mutate(
      { projectId, data: { label } },
      {
        onSuccess: () => {
          invalidate();
          setCreateOpen(false);
        },
      },
    );
  const rename = (label: string) => {
    if (renaming === null) {
      return;
    }
    updateKey.mutate(
      { projectId, keyId: renaming.id, data: { label } },
      {
        onSuccess: () => {
          invalidate();
          setRenaming(null);
        },
      },
    );
  };
  const revoke = (keyId: number) =>
    revokeKey.mutate({ projectId, keyId }, { onSuccess: invalidate });

  return (
    <section>
      <div className="flex items-start justify-between gap-4">
        <div>
          <h2 className="font-heading text-lg font-semibold text-foreground">
            {translate("title")}
          </h2>
          <p className="mt-1 text-sm text-muted-foreground">{translate("description")}</p>
        </div>
        <button type="button" onClick={() => setCreateOpen(true)} className={PRIMARY_BUTTON_CLASS}>
          {translate("create")}
        </button>
      </div>

      <div className="mt-4">
        <KeyListBody
          isPending={keys.isPending}
          isError={keys.isError}
          keys={keys.data?.data ?? []}
          publicId={publicId}
          onRename={setRenaming}
          onRevoke={revoke}
          revoking={revokeKey.isPending}
        />
      </div>

      <KeyNameDialog
        open={createOpen}
        onOpenChange={setCreateOpen}
        title={translate("createTitle")}
        initialName=""
        submitLabel={translate("createSubmit")}
        pending={createKey.isPending}
        onSubmit={create}
      />
      <KeyNameDialog
        open={renaming !== null}
        onOpenChange={(open) => {
          if (!open) {
            setRenaming(null);
          }
        }}
        title={translate("renameTitle")}
        initialName={renaming?.label ?? ""}
        submitLabel={translate("save")}
        pending={updateKey.isPending}
        onSubmit={rename}
      />
    </section>
  );
}

function KeyListBody({
  isPending,
  isError,
  keys,
  publicId,
  onRename,
  onRevoke,
  revoking,
}: {
  isPending: boolean;
  isError: boolean;
  keys: DsnKey[];
  publicId: string;
  onRename: (dsnKey: DsnKey) => void;
  onRevoke: (keyId: number) => void;
  revoking: boolean;
}) {
  const translate = useTranslations("settings.keys");
  if (isPending) {
    return <Notice>{translate("loading")}</Notice>;
  }
  if (isError) {
    return <Notice>{translate("error")}</Notice>;
  }
  if (keys.length === 0) {
    return <Notice>{translate("empty")}</Notice>;
  }
  return (
    <ul className="flex flex-col gap-3">
      {keys.map((dsnKey) => (
        <KeyRow
          key={dsnKey.id}
          dsnKey={dsnKey}
          publicId={publicId}
          onRename={onRename}
          onRevoke={onRevoke}
          revoking={revoking}
        />
      ))}
    </ul>
  );
}

function KeyRow({
  dsnKey,
  publicId,
  onRename,
  onRevoke,
  revoking,
}: {
  dsnKey: DsnKey;
  publicId: string;
  onRename: (dsnKey: DsnKey) => void;
  onRevoke: (keyId: number) => void;
  revoking: boolean;
}) {
  const translate = useTranslations("settings.keys");
  return (
    <li className="rounded-lg border border-border bg-card p-4">
      <div className="flex items-center justify-between gap-3">
        <span className="text-sm font-medium text-foreground">{dsnKey.label}</span>
        <div className="flex items-center gap-3">
          <span
            className={`text-xs uppercase ${dsnKey.isActive ? "text-info" : "text-muted-foreground"}`}
          >
            {dsnKey.isActive ? translate("active") : translate("revoked")}
          </span>
          {dsnKey.isActive ? (
            <>
              <button
                type="button"
                onClick={() => onRename(dsnKey)}
                className={SECONDARY_BUTTON_CLASS}
              >
                {translate("rename")}
              </button>
              <button
                type="button"
                onClick={() => onRevoke(dsnKey.id)}
                disabled={revoking}
                className={SECONDARY_BUTTON_CLASS}
              >
                {translate("revoke")}
              </button>
            </>
          ) : null}
        </div>
      </div>
      <div className="mt-2">
        <span className="text-xs uppercase text-muted-foreground">{translate("dsn")}</span>
        <code className="mt-1 block overflow-x-auto rounded bg-background p-2 text-xs text-muted-foreground">
          {buildDsn(dsnKey.publicKey, publicId)}
        </code>
      </div>
    </li>
  );
}
