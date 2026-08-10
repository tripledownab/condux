// Number and currency formatting shared across the dashboard. Generic helpers live here in src/lib,
// never reimplemented per feature.

const usd = new Intl.NumberFormat("en-US", {
  style: "currency",
  currency: "USD",
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});

const compact = new Intl.NumberFormat("en-US", { notation: "compact", maximumFractionDigits: 1 });

// A USD amount in cents precision, e.g. 17.5 becomes "$17.50". A run costing less than a cent rounds
// to "$0.00" (it truly rounds to zero cents); use finer precision only where sub-cent detail matters.
export function formatUsd(amount: number): string {
  return usd.format(amount);
}

// A large count in compact form, e.g. 1_500_000 becomes "1.5M"; small counts stay exact.
export function formatCompactNumber(value: number): string {
  return compact.format(value);
}
