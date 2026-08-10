"use client";

import { createContext, type ReactNode, useContext, useEffect, useState } from "react";

const STORAGE_KEY = "condux.selectedProjectId";

interface SelectedProject {
  selectedProjectId: number | null;
  selectProject: (projectId: number) => void;
}

const SelectedProjectContext = createContext<SelectedProject | null>(null);

// Holds the project the dashboard is scoped to, persisted in localStorage so it survives reloads. It
// starts null (server and first client render match, avoiding a hydration mismatch) and hydrates from
// storage on mount; useCurrentProject falls back to the org's first project until then and whenever the
// stored project is not in the current org.
export function SelectedProjectProvider({ children }: { children: ReactNode }) {
  const [selectedProjectId, setSelectedProjectId] = useState<number | null>(null);

  useEffect(() => {
    const stored = window.localStorage.getItem(STORAGE_KEY);
    if (stored !== null) {
      setSelectedProjectId(Number(stored));
    }
  }, []);

  const selectProject = (projectId: number) => {
    setSelectedProjectId(projectId);
    window.localStorage.setItem(STORAGE_KEY, String(projectId));
  };

  return (
    <SelectedProjectContext.Provider value={{ selectedProjectId, selectProject }}>
      {children}
    </SelectedProjectContext.Provider>
  );
}

export function useSelectedProject(): SelectedProject {
  const context = useContext(SelectedProjectContext);
  if (context === null) {
    throw new Error("useSelectedProject must be used within a SelectedProjectProvider");
  }
  return context;
}
