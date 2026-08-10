import { useEffect, useRef } from "react";

export type ShortcutHandler = (event: KeyboardEvent) => void;
// A map of KeyboardEvent.key (e.g. "j", "Enter", "?") to its handler.
export type ShortcutMap = Record<string, ShortcutHandler>;

// Single source for registering global keyboard shortcuts. Ignores events while the user is typing in a
// field (input/textarea/select/contenteditable) or holding a modifier, so typing a note never triggers
// navigation. Handlers are read through a ref, so callers may pass fresh inline closures each render
// without re-binding the listener; the listener itself is bound once (per `enabled`).
export function useKeyboardShortcuts(shortcuts: ShortcutMap, enabled = true): void {
  const latest = useRef(shortcuts);
  latest.current = shortcuts;

  useEffect(() => {
    if (!enabled) {
      return;
    }
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.metaKey || event.ctrlKey || event.altKey || isTypingTarget(event.target)) {
        return;
      }
      const handler = latest.current[event.key];
      if (handler !== undefined) {
        event.preventDefault();
        handler(event);
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [enabled]);
}

// True while focus is in a text-entry surface, where a keystroke is content, not a command.
function isTypingTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) {
    return false;
  }
  const tag = target.tagName;
  return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || target.isContentEditable;
}
