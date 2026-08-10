import assert from "node:assert/strict";
import { test } from "node:test";

import { InvalidLineItemError, orderTotalCents } from "./checkout.mjs";

test("orderTotalCents sums line item prices", () => {
  const total = orderTotalCents({
    items: [
      { sku: "SKU-MOUSE-1", price: { cents: 2999 } },
      { sku: "SKU-CABLE-3", price: { cents: 899 } },
    ],
  });
  assert.equal(total, 3898);
});

test("orderTotalCents throws a descriptive error for a missing price instead of a TypeError", () => {
  assert.throws(
    () => orderTotalCents({ items: [{ sku: "SKU-DESK-7" }] }),
    (error) => error instanceof InvalidLineItemError && /SKU-DESK-7/.test(error.message),
  );
});

test("orderTotalCents throws when price.cents is not a number", () => {
  assert.throws(
    () => orderTotalCents({ items: [{ sku: "SKU-DESK-7", price: {} }] }),
    InvalidLineItemError,
  );
});
