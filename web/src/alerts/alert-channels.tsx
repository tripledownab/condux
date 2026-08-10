"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import {
  getListAlertRulesQueryKey,
  useDeleteAlertChannel,
  useTestAlertChannel,
  useUpdateAlertChannel,
} from "@/src/api/generated/condux";
import type { AlertChannelResponse } from "@/src/api/generated/model";
import { ChannelList, ChannelRow } from "@/src/channels/channel-list";
import { CHANNEL_OPTIONS } from "@/src/channels/format";
import { TemplateDialog } from "@/src/channels/template-dialog";
import { TestChannelButton, TestStatus, testStatus } from "@/src/channels/test-channel-button";
import { FIELD_CLASS, PRIMARY_BUTTON_CLASS, SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { Combobox } from "@/src/components/ui/combobox";

// A rule's notification channels: the shared list + add form, wired to the alert-channel API and scoped to
// the rule. Every mutation invalidates the rules list. Matches the org notification channels UI.
export function AlertChannels({
  channels,
  projectId,
  ruleId,
}: {
  channels: AlertChannelResponse[];
  projectId: number;
  ruleId: string;
}) {
  const queryClient = useQueryClient();
  const invalidate = () =>
    queryClient.invalidateQueries({ queryKey: getListAlertRulesQueryKey(projectId) });

  return (
    <ChannelList isEmpty={channels.length === 0}>
      {channels.map((channel) => (
        <AlertChannelRow
          key={channel.id}
          channel={channel}
          projectId={projectId}
          ruleId={ruleId}
          onChanged={invalidate}
        />
      ))}
    </ChannelList>
  );
}

function AlertChannelRow({
  channel,
  projectId,
  ruleId,
  onChanged,
}: {
  channel: AlertChannelResponse;
  projectId: number;
  ruleId: string;
  onChanged: () => void;
}) {
  const translate = useTranslations("settings.channels");
  const deleteChannel = useDeleteAlertChannel();
  const testChannel = useTestAlertChannel();
  const updateChannel = useUpdateAlertChannel();
  const [status, setStatus] = useState(TestStatus.Idle);
  const [editing, setEditing] = useState(false);
  const [templateOpen, setTemplateOpen] = useState(false);

  const saveTemplate = (template: string) =>
    updateChannel.mutate(
      {
        projectId,
        ruleId,
        channelId: channel.id,
        data: { channel: channel.channel, target: channel.target, template },
      },
      {
        onSuccess: () => {
          onChanged();
          setTemplateOpen(false);
        },
      },
    );

  const test = () => {
    setStatus(TestStatus.Sending);
    testChannel.mutate(
      { projectId, ruleId, channelId: channel.id },
      {
        onSuccess: (response) =>
          setStatus(
            response.status === 200
              ? testStatus(response.data.delivered, response.data.error)
              : TestStatus.Failed,
          ),
        onError: () => setStatus(TestStatus.Failed),
      },
    );
  };

  if (editing) {
    return (
      <EditChannelRow
        channel={channel}
        pending={updateChannel.isPending}
        onCancel={() => setEditing(false)}
        onSave={(type, target) =>
          updateChannel.mutate(
            {
              projectId,
              ruleId,
              channelId: channel.id,
              data: { channel: type, target, template: channel.template ?? null },
            },
            {
              onSuccess: () => {
                onChanged();
                setEditing(false);
              },
            },
          )
        }
      />
    );
  }

  return (
    <ChannelRow channel={channel.channel} target={channel.target}>
      <TestChannelButton status={status} onSend={test} />
      <button
        type="button"
        onClick={() => setTemplateOpen(true)}
        className={SECONDARY_BUTTON_CLASS}
      >
        {translate("templateAction")}
      </button>
      <TemplateDialog
        open={templateOpen}
        onOpenChange={setTemplateOpen}
        channel={channel.channel}
        currentTemplate={channel.template}
        pending={updateChannel.isPending}
        onSave={saveTemplate}
      />
      <button type="button" onClick={() => setEditing(true)} className={SECONDARY_BUTTON_CLASS}>
        {translate("edit")}
      </button>
      <button
        type="button"
        onClick={() =>
          deleteChannel.mutate(
            { projectId, ruleId, channelId: channel.id },
            { onSuccess: onChanged },
          )
        }
        disabled={deleteChannel.isPending}
        className={SECONDARY_BUTTON_CLASS}
      >
        {translate("remove")}
      </button>
    </ChannelRow>
  );
}

// Inline editor for a channel's type + target, rendered as the row while editing. Prefilled from the
// channel; Save calls updateAlertChannel, Cancel restores the row.
function EditChannelRow({
  channel,
  onSave,
  onCancel,
  pending,
}: {
  channel: AlertChannelResponse;
  onSave: (channel: number, target: string) => void;
  onCancel: () => void;
  pending: boolean;
}) {
  const translate = useTranslations("settings.channels");
  const tCommon = useTranslations("common");
  const [type, setType] = useState<number>(channel.channel);
  const [target, setTarget] = useState(channel.target);
  const channelOptions = CHANNEL_OPTIONS.map((option) => ({
    value: String(option.value),
    label: translate(option.key),
  }));

  const submit = (event: React.FormEvent) => {
    event.preventDefault();
    const trimmed = target.trim();
    if (trimmed === "") {
      return;
    }
    onSave(type, trimmed);
  };

  return (
    <li>
      <form onSubmit={submit} className="flex flex-wrap items-end gap-2">
        <div className="flex flex-col gap-1 text-sm">
          <span className="text-xs uppercase text-muted-foreground">
            {translate("channelLabel")}
          </span>
          <Combobox
            value={String(type)}
            onValueChange={(value) => setType(Number(value))}
            options={channelOptions}
            aria-label={translate("channelLabel")}
            searchPlaceholder={tCommon("comboboxSearch")}
            emptyText={tCommon("comboboxEmpty")}
          />
        </div>
        <label className="flex flex-1 flex-col gap-1 text-sm">
          <span className="text-xs uppercase text-muted-foreground">
            {translate("targetLabel")}
          </span>
          <input
            aria-label={translate("targetLabel")}
            value={target}
            onChange={(event) => setTarget(event.target.value)}
            className={FIELD_CLASS}
          />
        </label>
        <button type="submit" disabled={pending} className={PRIMARY_BUTTON_CLASS}>
          {translate("save")}
        </button>
        <button type="button" onClick={onCancel} className={SECONDARY_BUTTON_CLASS}>
          {translate("cancel")}
        </button>
      </form>
    </li>
  );
}
