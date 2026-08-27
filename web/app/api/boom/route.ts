// The dogfood lever: forces a genuine server error so Condux reports on itself end to end.
// Hit http://localhost:3000/api/boom and watch the issue land in the Issues surface (requires
// CONDUX_DSN to be set for the dashboard server, pointing at a project you can watch).
export function GET(): Response {
  chargeOrder({ id: "ord-dogfood-1" });
  return new Response("unreachable");
}

function chargeOrder(order: { id: string; total?: { amount: number } }): number {
  // Deliberate: total is absent, so this dereference throws the classic TypeError.
  const total = order.total as { amount: number };
  return total.amount;
}
