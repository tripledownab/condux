import assert from "node:assert/strict";
import { test } from "node:test";
import { installErrorHandlers } from "../src/index.ts";

// A fake global target that records listeners so a test can emit synthetic error events (no real DOM).
function fakeTarget() {
  const listeners: Record<string, (event: unknown) => void> = {};
  return {
    target: {
      addEventListener(type: string, listener: (event: unknown) => void) {
        listeners[type] = listener;
      },
    },
    emit(type: string, event: unknown) {
      listeners[type]?.(event);
    },
  };
}

function recordingCapture() {
  const captured: { error: unknown; handled?: boolean }[] = [];
  const capture = async (error: unknown, handled?: boolean) => {
    captured.push({ error, handled });
    return { ok: true, attempts: 1 };
  };
  return { capture, captured };
}

test("installErrorHandlers reports window error + unhandledrejection as unhandled", () => {
  const { target, emit } = fakeTarget();
  const { capture, captured } = recordingCapture();
  installErrorHandlers(target, capture);

  const err = new Error("boom");
  emit("error", { error: err });
  emit("unhandledrejection", { reason: err });

  assert.equal(captured.length, 2);
  assert.equal(captured[0].error, err);
  assert.equal(captured[0].handled, false);
  assert.equal(captured[1].error, err);
  assert.equal(captured[1].handled, false);
});

test("installErrorHandlers falls back to the message when there is no error object", () => {
  const { target, emit } = fakeTarget();
  const { capture, captured } = recordingCapture();
  installErrorHandlers(target, capture);

  emit("error", { message: "Script error." });

  assert.equal(captured[0].error, "Script error.");
  assert.equal(captured[0].handled, false);
});
