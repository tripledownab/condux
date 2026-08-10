import {
  useBillingCheckout,
  useBillingPortal,
  useBillingStatus as useGeneratedBillingStatus,
} from "@/src/api/generated/condux";

// Billing hooks for the Plan section. Thin adapters over the generated TanStack Query client (the single
// source of truth for the API), keeping the shape the component wants: status unwrapped from the response
// envelope, and a checkout that redirects to Stripe on success.

// The org's plan + which tiers can be self-served. A fetch failure just hides the upgrade buttons (billing
// stays optional), so it does not retry. `select` unwraps the response envelope to the BillingStatusResponse.
export function useBillingStatus(orgId: number) {
  return useGeneratedBillingStatus(orgId, {
    query: { select: (response) => response.data, retry: false },
  });
}

// Starts a Stripe Checkout for a tier and hands the browser to Stripe's hosted page; the tier is applied
// to the org asynchronously by the Stripe webhook once payment completes. Exposes a tier-only `mutate`.
export function useCheckout(orgId: number) {
  const checkout = useBillingCheckout({
    mutation: {
      onSuccess: (response) => {
        if (response.status === 200) {
          window.location.href = response.data.url;
        }
      },
    },
  });
  return {
    isPending: checkout.isPending,
    isError: checkout.isError,
    mutate: (tier: string) => checkout.mutate({ orgId, data: { tier } }),
  };
}

// Opens Stripe's hosted billing portal for the org's customer and hands the browser to it, so an admin can
// change/downgrade/cancel the plan, update the card and view invoices. Tier changes reconcile via the webhook.
export function usePortal(orgId: number) {
  const portal = useBillingPortal({
    mutation: {
      onSuccess: (response) => {
        if (response.status === 200) {
          window.location.href = response.data.url;
        }
      },
    },
  });
  return {
    isPending: portal.isPending,
    isError: portal.isError,
    open: () => portal.mutate({ orgId }),
  };
}

// Tier ordering (matches Condux.Core.Plans.Tier) so we only offer upgrades above the current plan.
const TIER_RANK: Record<string, number> = { Free: 0, Team: 1, Business: 2, Enterprise: 3 };

export const tierRank = (name: string): number => TIER_RANK[name] ?? 0;
