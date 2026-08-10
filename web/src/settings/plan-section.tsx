"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useEffect, useState } from "react";
import { getListMyOrgsQueryKey } from "@/src/api/generated/condux";
import { tierRank, useBillingStatus, useCheckout, usePortal } from "@/src/billing/use-billing";
import { Button } from "@/src/components/ui/button";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/src/components/ui/dialog";
import { planName } from "@/src/orgs/plan-format";

// The Plan section of Settings -> General: the current plan plus self-serve upgrade buttons when Stripe
// billing is configured on the server (#billing). Clicking an upgrade opens Stripe Checkout; the tier is
// applied by the webhook after payment, so on return we just refresh the org. When billing isn't
// configured, it degrades to the static "see pricing" note.
export function PlanSection({
  orgId,
  tier,
  canManage,
}: {
  orgId: number;
  tier: number;
  canManage: boolean;
}) {
  const translate = useTranslations("settings.general");
  const queryClient = useQueryClient();
  const billing = useBillingStatus(orgId);
  const checkout = useCheckout(orgId);
  const portal = usePortal(orgId);

  // A checkout returns to /settings/general?billing=success|cancelled. Read it once (via window, so the
  // page needs no Suspense boundary) and, on success, refresh the org so the new tier shows once the
  // webhook has applied it.
  const [outcome, setOutcome] = useState<"success" | "cancelled" | null>(null);
  useEffect(() => {
    const value = new URLSearchParams(window.location.search).get("billing");
    if (value === "success" || value === "cancelled") {
      setOutcome(value);
      if (value === "success") {
        queryClient.invalidateQueries({ queryKey: getListMyOrgsQueryKey() });
      }
    }
  }, [queryClient]);

  const hasSubscription = billing.data?.hasSubscription ?? false;
  const upgrades = (billing.data?.purchasableTiers ?? []).filter((name) => tierRank(name) > tier);
  // A subscriber changes plan (up or down), cancels, updates their card and sees invoices through the
  // Stripe portal — a fresh checkout would spin up a SECOND subscription — so the checkout upgrade buttons
  // are only for orgs without one yet.
  const showUpgrades = Boolean(
    billing.data?.billingEnabled && canManage && !hasSubscription && upgrades.length > 0,
  );
  const showManage = Boolean(billing.data?.billingEnabled && canManage && hasSubscription);

  return (
    <section>
      <h2 className="font-heading text-lg font-semibold text-foreground">
        {translate("planTitle")}
      </h2>
      <p className="mt-2 text-sm text-foreground">
        {translate("planCurrent", { plan: planName(tier) })}
      </p>

      {/* A successful checkout returns here; welcome the subscriber with a modal rather than a note the
          eye skims past. The tier is applied by the webhook, so the plan above refreshes on its own. */}
      <Dialog
        open={outcome === "success"}
        onOpenChange={(open) => {
          if (!open) {
            setOutcome(null);
          }
        }}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{translate("billingThanksTitle")}</DialogTitle>
            <DialogDescription>{translate("billingThanksBody")}</DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <DialogClose asChild>
              <Button>{translate("billingThanksDone")}</Button>
            </DialogClose>
          </DialogFooter>
        </DialogContent>
      </Dialog>
      {/* Returned from a cancelled checkout — reassure that nothing was charged rather than leave them
          wondering. Kept lighter than the success modal (no celebration), but a dialog for symmetry. */}
      <Dialog
        open={outcome === "cancelled"}
        onOpenChange={(open) => {
          if (!open) {
            setOutcome(null);
          }
        }}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{translate("billingCancelledTitle")}</DialogTitle>
            <DialogDescription>{translate("billingCancelledBody")}</DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <DialogClose asChild>
              <Button variant="outline">{translate("billingThanksDone")}</Button>
            </DialogClose>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {showManage ? (
        <div className="mt-3">
          <button
            type="button"
            disabled={portal.isPending}
            onClick={portal.open}
            className="rounded-md border border-border px-4 py-2 text-sm font-medium text-foreground transition-colors hover:bg-card disabled:opacity-50"
          >
            {translate("planManage")}
          </button>
        </div>
      ) : showUpgrades ? (
        <div className="mt-3 flex flex-wrap gap-2">
          {upgrades.map((name) => (
            <button
              key={name}
              type="button"
              disabled={checkout.isPending}
              onClick={() => checkout.mutate(name)}
              className="rounded-md bg-primary px-4 py-2 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary/80 disabled:opacity-50"
            >
              {translate("planUpgrade", { plan: name })}
            </button>
          ))}
        </div>
      ) : hasSubscription ? (
        // Has a subscription but this member cannot manage it (admin+ only).
        <p className="mt-2 text-xs text-muted-foreground">{translate("planManaged")}</p>
      ) : (
        <p className="mt-1 text-xs text-muted-foreground">{translate("planNote")}</p>
      )}
      {checkout.isError ? (
        <p role="alert" className="mt-2 text-sm text-error">
          {translate("planCheckoutFailed")}
        </p>
      ) : null}
      {portal.isError ? (
        <p role="alert" className="mt-2 text-sm text-error">
          {translate("planManageFailed")}
        </p>
      ) : null}
    </section>
  );
}
