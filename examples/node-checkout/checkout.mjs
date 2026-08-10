// A tiny checkout service that reports realistic errors to Condux with @condux/node.
//
// It processes a batch of orders through inventory + payment. Some orders fail the same way
// (a declining card, an out-of-stock SKU), so they group into one issue with a rising count; one
// order has a line item synced without a price, which we now surface as a clear domain error. Each
// failure is reported with its real stack trace, and a couple of captureMessage calls add non-error
// signal. Run against a local stack:
//
//   pnpm install
//   CONDUX_DSN="http://<key>@localhost:9010/<projectId>" pnpm start

import { pathToFileURL } from "node:url";

import { Level, captureException, captureMessage, init } from "@condux/node";

const RELEASE = "checkout-web@2.4.1";
const OUT_OF_STOCK_SKUS = new Set(["SKU-GPU-40", "SKU-KEYBOARD-9"]);
const DECLINING_CARDS = new Set(["4000000000000002"]);

class PaymentDeclinedError extends Error {
  constructor(reason) {
    super(`card declined: ${reason}`);
    this.name = "PaymentDeclinedError";
  }
}

class OutOfStockError extends Error {
  constructor(sku) {
    super(`no inventory for ${sku}`);
    this.name = "OutOfStockError";
  }
}

class InvalidLineItemError extends Error {
  constructor(sku) {
    super(`line item ${sku ?? "<unknown>"} is missing a price`);
    this.name = "InvalidLineItemError";
  }
}

function orderTotalCents(order) {
  // A line item synced without a price used to crash on `.cents`. Validate the price so the bad
  // data surfaces as a clear, groupable domain error instead of an undefined dereference.
  return order.items.reduce((sum, item) => {
    if (!item.price || typeof item.price.cents !== "number") {
      throw new InvalidLineItemError(item.sku);
    }
    return sum + item.price.cents;
  }, 0);
}

function reserveInventory(item) {
  if (OUT_OF_STOCK_SKUS.has(item.sku)) {
    throw new OutOfStockError(item.sku);
  }
}

function chargeCard(order) {
  if (DECLINING_CARDS.has(order.card)) {
    throw new PaymentDeclinedError("insufficient_funds");
  }
}

function checkout(order) {
  const amountCents = orderTotalCents(order);
  for (const item of order.items) {
    reserveInventory(item);
  }
  chargeCard(order);
  return amountCents;
}

const ORDERS = [
  { id: "ord_1001", card: "4111111111111111", items: [{ sku: "SKU-MOUSE-1", price: { cents: 2999 } }] },
  { id: "ord_1002", card: "4000000000000002", items: [{ sku: "SKU-MOUSE-1", price: { cents: 2999 } }] },
  { id: "ord_1003", card: "4000000000000002", items: [{ sku: "SKU-CABLE-3", price: { cents: 899 } }] },
  { id: "ord_1004", card: "4111111111111111", items: [{ sku: "SKU-GPU-40", price: { cents: 149900 } }] },
  { id: "ord_1005", card: "4111111111111111", items: [{ sku: "SKU-KEYBOARD-9", price: { cents: 8900 } }] },
  { id: "ord_1006", card: "4111111111111111", items: [{ sku: "SKU-DESK-7" }] }, // missing price
  { id: "ord_1007", card: "4000000000000002", items: [{ sku: "SKU-CABLE-3", price: { cents: 899 } }] },
  { id: "ord_1008", card: "4111111111111111", items: [{ sku: "SKU-MONITOR-2", price: { cents: 25900 } }] },
];

async function main() {
  const dsn = process.env.CONDUX_DSN;
  if (!dsn) {
    console.error("Set CONDUX_DSN to a project DSN, e.g. http://<key>@localhost:9010/<projectId>");
    process.exit(1);
  }
  init({ dsn, environment: process.env.CONDUX_ENV ?? "production", release: RELEASE });

  const pending = [];
  let succeeded = 0;
  for (const order of ORDERS) {
    try {
      checkout(order);
      succeeded += 1;
    } catch (error) {
      pending.push(captureException(error));
    }
  }

  if (succeeded / ORDERS.length < 0.8) {
    const rate = Math.round((succeeded / ORDERS.length) * 100);
    pending.push(captureMessage(`checkout success rate ${rate}% below target`, Level.Warning));
  }
  pending.push(captureMessage(`checkout batch complete: ${succeeded}/${ORDERS.length} succeeded`, Level.Info));

  const delivered = (await Promise.all(pending)).filter((result) => result.ok).length;
  console.log(`reported ${delivered}/${pending.length} events to Condux (release ${RELEASE})`);
}

export { InvalidLineItemError, OutOfStockError, PaymentDeclinedError, checkout, orderTotalCents };

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) {
  main();
}
