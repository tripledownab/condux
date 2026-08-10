"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import {
  getListNotificationChannelsQueryKey,
  useAddNotificationChannel,
  useDeleteNotificationChannel,
  useListNotificationChannels,
  useTestNotificationChannel,
} from "@/src/api/generated/condux";
import type { NotificationChannelResponse } from "@/src/api/generated/model";
import { AddChannelForm } from "@/src/channels/add-channel-form";
import { ChannelList, ChannelRow } from "@/src/channels/channel-list";
import { TestChannelButton, TestStatus, testStatus } from "@/src/channels/test-channel-button";
import { SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { Notice } from "@/src/components/notice";
import { OrgStatus, useCurrentOrg } from "@/src/orgs/current-org";
import { WeeklySummarySettings } from "./weekly-summary-settings";

// The Notifications settings tab (#129): the org's delivery channels for operational notices — today a
// Conductor pause when the AI-fix cost cap or allowance is reached in auto mode (#130). Org-scoped;
// admins add/remove/test channels, members can view. Shares the channel UI with the alert-rule channels.
export function NotificationSettings() {
  const translate = useTranslations("settings.notifications");
  const current = useCurrentOrg();

  if (current.status === OrgStatus.Loading) {
    return <Notice>{translate("loading")}</Notice>;
  }
  if (current.status === OrgStatus.Error) {
    return <Notice>{translate("error")}</Notice>;
  }
  if (current.status === OrgStatus.NoOrg) {
    return <Notice>{translate("noOrg")}</Notice>;
  }

  const canManage = current.role === "owner" || current.role === "admin";
  return (
    <div className="flex flex-col gap-8">
      <Channels orgId={current.org.id} canManage={canManage} />
      <WeeklySummarySettings org={current.org} canManage={canManage} />
    </div>
  );
}

function Channels({ orgId, canManage }: { orgId: number; canManage: boolean }) {
  const translate = useTranslations("settings.notifications");
  const queryClient = useQueryClient();
  const channels = useListNotificationChannels(orgId);
  const addChannel = useAddNotificationChannel();

  const list: NotificationChannelResponse[] = channels.data?.data ?? [];
  const invalidate = () =>
    queryClient.invalidateQueries({ queryKey: getListNotificationChannelsQueryKey(orgId) });

  return (
    <section className="flex flex-col gap-4">
      <div>
        <h2 className="font-heading text-lg font-semibold text-foreground">{translate("title")}</h2>
        <p className="mt-1 text-sm text-muted-foreground">{translate("description")}</p>
      </div>

      <ChannelList isEmpty={list.length === 0}>
        {list.map((channel) => (
          <NotificationChannelRow
            key={channel.id}
            channel={channel}
            orgId={orgId}
            canManage={canManage}
            onChanged={invalidate}
          />
        ))}
      </ChannelList>

      {canManage ? (
        <AddChannelForm
          pending={addChannel.isPending}
          onAdd={(channel, target) =>
            addChannel.mutate({ orgId, data: { channel, target } }, { onSuccess: invalidate })
          }
        />
      ) : null}
    </section>
  );
}

function NotificationChannelRow({
  channel,
  orgId,
  canManage,
  onChanged,
}: {
  channel: NotificationChannelResponse;
  orgId: number;
  canManage: boolean;
  onChanged: () => void;
}) {
  const translate = useTranslations("settings.channels");
  const deleteChannel = useDeleteNotificationChannel();
  const testChannel = useTestNotificationChannel();
  const [status, setStatus] = useState(TestStatus.Idle);

  const test = () => {
    setStatus(TestStatus.Sending);
    testChannel.mutate(
      { orgId, id: channel.id },
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

  return (
    <ChannelRow channel={channel.channel} target={channel.target}>
      {canManage ? (
        <>
          <TestChannelButton status={status} onSend={test} />
          <button
            type="button"
            onClick={() =>
              deleteChannel.mutate({ orgId, id: channel.id }, { onSuccess: onChanged })
            }
            disabled={deleteChannel.isPending}
            className={SECONDARY_BUTTON_CLASS}
          >
            {translate("remove")}
          </button>
        </>
      ) : null}
    </ChannelRow>
  );
}
