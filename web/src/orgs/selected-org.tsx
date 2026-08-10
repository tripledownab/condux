"use client";

import { createContext, type ReactNode, useContext, useEffect, useState } from "react";

const STORAGE_KEY = "condux.selectedOrgId";

interface SelectedOrg {
  selectedOrgId: number | null;
  selectOrg: (orgId: number) => void;
}

const SelectedOrgContext = createContext<SelectedOrg | null>(null);

// Holds the org the dashboard is scoped to, persisted in localStorage so it survives reloads. It starts
// null (server and first client render match, avoiding a hydration mismatch) and hydrates from storage on
// mount; useCurrentProject falls back to the first membership until then and whenever the stored org is
// not one the user still belongs to. Switching org lets the project fall back to that org's first project.
export function SelectedOrgProvider({ children }: { children: ReactNode }) {
  const [selectedOrgId, setSelectedOrgId] = useState<number | null>(null);

  useEffect(() => {
    const stored = window.localStorage.getItem(STORAGE_KEY);
    if (stored !== null) {
      setSelectedOrgId(Number(stored));
    }
  }, []);

  const selectOrg = (orgId: number) => {
    setSelectedOrgId(orgId);
    window.localStorage.setItem(STORAGE_KEY, String(orgId));
  };

  return (
    <SelectedOrgContext.Provider value={{ selectedOrgId, selectOrg }}>
      {children}
    </SelectedOrgContext.Provider>
  );
}

export function useSelectedOrg(): SelectedOrg {
  const context = useContext(SelectedOrgContext);
  if (context === null) {
    throw new Error("useSelectedOrg must be used within a SelectedOrgProvider");
  }
  return context;
}
