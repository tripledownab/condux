import { renderHook } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { useKeyboardShortcuts } from "./use-keyboard-shortcuts";

function press(key: string, target: EventTarget = document.body, init: KeyboardEventInit = {}) {
  target.dispatchEvent(
    new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true, ...init }),
  );
}

describe("useKeyboardShortcuts", () => {
  it("runs the handler for a mapped key", () => {
    const j = vi.fn();
    renderHook(() => useKeyboardShortcuts({ j }));
    press("j");
    expect(j).toHaveBeenCalledOnce();
  });

  it("ignores keys while typing in a field", () => {
    const j = vi.fn();
    renderHook(() => useKeyboardShortcuts({ j }));
    const input = document.createElement("input");
    document.body.appendChild(input);
    press("j", input);
    expect(j).not.toHaveBeenCalled();
    input.remove();
  });

  it("ignores keys held with a modifier", () => {
    const j = vi.fn();
    renderHook(() => useKeyboardShortcuts({ j }));
    press("j", document.body, { metaKey: true });
    expect(j).not.toHaveBeenCalled();
  });

  it("stops listening after unmount", () => {
    const j = vi.fn();
    const { unmount } = renderHook(() => useKeyboardShortcuts({ j }));
    unmount();
    press("j");
    expect(j).not.toHaveBeenCalled();
  });

  it("does nothing when disabled", () => {
    const j = vi.fn();
    renderHook(() => useKeyboardShortcuts({ j }, false));
    press("j");
    expect(j).not.toHaveBeenCalled();
  });
});
