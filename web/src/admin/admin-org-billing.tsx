"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import {
  getAdminBillingStatusQueryKey,
  getAdminOrgDetailQueryKey,
  useAdminBillingCancel,
  useAdminBillingChangePlan,
  useAdminBillingPortal,
  useAdminBillingStatus,
} from "@/src/api/generated/condux";
import type { AdminPortalResponse } from "@/src/api/generated/model";
import { ConfirmDialog } from "@/src/components/confirm-dialog";
import { PRIMARY_BUTTON_CLASS, SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { Notice } from "@/src/components/notice";
import { Combobox } from "@/src/components/ui/combobox";

// Full billing control for the admin detail page (ADR-0027): cancel the subscription (at period end),
// change its plan, or open the Stripe portal for anything deeper. Every action drives the Stripe API; the
// tier itself is reconciled by the webhook, so nothing here writes it directly.
export function AdminOrgBilling({ orgId }: { orgId: number }) {
  const translate = useTranslations("admin.orgDetail.billing");
  const tCommon = useTranslations("common");
  const queryClient = useQueryClient();
  const billing = useAdminBillingStatus(orgId);
  const cancel = useAdminBillingCancel();
  const changePlan = useAdminBillingChangePlan();
  const portal = useAdminBillingPortal();
  const [cancelOpen, setCancelOpen] = useState(false);
  const [tier, setTier] = useState("");

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: getAdminBillingStatusQueryKey(orgId) });
    queryClient.invalidateQueries({ queryKey: getAdminOrgDetailQueryKey(orgId) });
  };

  const status = billing.data?.data;
  const heading = (
    <h2 className="font-heading text-lg font-semibold text-foreground">{translate("title")}</h2>
  );

  if (billing.isPending) {
    return (
      <section>
        {heading}
        <Notice>{translate("loading")}</Notice>
      </section>
    );
  }
  if (billing.isError || !status || !status.billingEnabled) {
    return (
      <section>
        {heading}
        <Notice>{translate("disabled")}</Notice>
      </section>
    );
  }

  const selectedTier = tier || status.purchasableTiers[0] || "";
  const tierOptions = status.purchasableTiers.map((option) => ({ value: option, label: option }));
  return (
    <section className="flex flex-col gap-3">
      {heading}
      <p className="text-sm text-foreground">
        {translate("plan", { tier: status.tier })}{" "}
        <span className="text-muted-foreground">
          {status.hasSubscription ? translate("subscribed") : translate("noSubscription")}
        </span>
      </p>

      {status.hasSubscription ? (
        <div className="flex flex-wrap items-end gap-2">
          <Combobox
            value={selectedTier}
            onValueChange={setTier}
            options={tierOptions}
            aria-label={translate("changePlan")}
            className="w-44"
            searchPlaceholder={tCommon("comboboxSearch")}
            emptyText={tCommon("comboboxEmpty")}
          />
          <button
            type="button"
            disabled={changePlan.isPending || selectedTier === ""}
            onClick={() =>
              changePlan.mutate({ orgId, data: { tier: selectedTier } }, { onSuccess: invalidate })
            }
            className={SECONDARY_BUTTON_CLASS}
          >
            {translate("apply")}
          </button>
          <button
            type="button"
            disabled={cancel.isPending}
            onClick={() => setCancelOpen(true)}
            className={SECONDARY_BUTTON_CLASS}
          >
            {translate("cancel")}
          </button>
        </div>
      ) : null}

      <div>
        <button
          type="button"
          disabled={portal.isPending || !status.stripeCustomerId}
          onClick={() =>
            portal.mutate(
              { orgId },
              {
                onSuccess: (envelope) => {
                  const body = envelope.data as AdminPortalResponse | undefined;
                  if (body?.url) {
                    window.location.href = body.url;
                  }
                },
              },
            )
          }
          className={PRIMARY_BUTTON_CLASS}
        >
          {translate("portal")}
        </button>
      </div>

      {cancel.isError || changePlan.isError || portal.isError ? (
        <p role="alert" className="text-sm text-error">
          {translate("failed")}
        </p>
      ) : null}

      <ConfirmDialog
        open={cancelOpen}
        onOpenChange={setCancelOpen}
        title={translate("cancelConfirmTitle")}
        body={translate("cancelConfirmBody")}
        confirmLabel={translate("cancel")}
        cancelLabel={translate("keep")}
        pending={cancel.isPending}
        destructive
        onConfirm={() =>
          cancel.mutate(
            { orgId },
            {
              onSuccess: () => {
                setCancelOpen(false);
                invalidate();
              },
            },
          )
        }
      />
    </section>
  );
}
