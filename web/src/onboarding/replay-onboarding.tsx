"use client";

import { createContext, type ReactNode, useContext, useEffect, useState } from "react";

const STORAGE_KEY = "condux.replayOnboarding";

// Developer-only escape hatch. Onboarding "done" is otherwise the server-recorded `me.onboarded` flag
// (users.onboarded_at, see useOnboarding); this forces "not done" regardless, so the gate re-engages and
// you can walk the first-run flow again without a fresh account. Honoured ONLY outside production (next
// dev): a stray flag can never gate a real user.
const IS_DEV = process.env.NODE_ENV !== "production";

interface ReplayOnboarding {
  // Whether the control exists at all (dev builds only); the sidebar/menu hide it in production.
  available: boolean;
  // True while a developer is replaying onboarding, which forces the gate on.
  replaying: boolean;
  setReplaying: (on: boolean) => void;
}

const ReplayOnboardingContext = createContext<ReplayOnboarding | null>(null);

// Starts off (server and first client render match, avoiding a hydration mismatch) and hydrates from
// localStorage on mount, mirroring the selected-org/collapsed-sidebar preferences.
export function ReplayOnboardingProvider({ children }: { children: ReactNode }) {
  const [replaying, setReplayingState] = useState(false);

  useEffect(() => {
    if (IS_DEV) {
      setReplayingState(window.localStorage.getItem(STORAGE_KEY) === "true");
    }
  }, []);

  const setReplaying = (on: boolean) => {
    if (!IS_DEV) return;
    setReplayingState(on);
    window.localStorage.setItem(STORAGE_KEY, String(on));
  };

  return (
    <ReplayOnboardingContext.Provider
      value={{ available: IS_DEV, replaying: IS_DEV && replaying, setReplaying }}
    >
      {children}
    </ReplayOnboardingContext.Provider>
  );
}

export function useReplayOnboarding(): ReplayOnboarding {
  const context = useContext(ReplayOnboardingContext);
  if (context === null) {
    throw new Error("useReplayOnboarding must be used within a ReplayOnboardingProvider");
  }
  return context;
}
