// Fixture source for the symbolizer tests: one inlined call inside an exported one, so a single
// address resolves to two frames.
static inline __attribute__((always_inline)) int apply_discount(int sum) {
  if (sum > 100) {
    __builtin_trap();
  }
  return sum;
}

int cart_total(int a, int b) {
  return apply_discount(a + b);
}
