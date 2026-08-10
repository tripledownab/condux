"use client";

import { RiArrowDownSLine, RiLogoutBoxRLine, RiRestartLine } from "@remixicon/react";
import { useQueryClient } from "@tanstack/react-query";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { getMeQueryKey, useLogout, useMe } from "@/src/api/generated/condux";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/src/components/ui/dropdown-menu";
import { useReplayOnboarding } from "@/src/onboarding/replay-onboarding";
import { ROUTES } from "@/src/routes";

// The account control at the sidebar's base (layout 30's user card): a Radix dropdown whose trigger
// is the card (letter avatar + email) or, when the sidebar is collapsed, just the avatar. The menu
// signs out: logout revokes the session server-side, then we drop the cached /api/auth/me and send
// the user to the login page.
export function UserMenu({ compact }: { compact?: boolean }) {
  const translate = useTranslations("user");
  const router = useRouter();
  const queryClient = useQueryClient();
  const { data } = useMe();
  const logout = useLogout();
  const replay = useReplayOnboarding();

  const email = data?.data?.email;
  const avatarLetter = (email ?? "?").charAt(0).toUpperCase();

  const signOut = () => {
    logout.mutate(undefined, {
      onSuccess: async () => {
        await queryClient.invalidateQueries({ queryKey: getMeQueryKey() });
        router.replace(ROUTES.login);
      },
    });
  };

  // Dev-only: flip the replay flag so the onboarding gate re-engages (or release it), navigating to the
  // page that matches. Never rendered in production (replay.available is false there).
  const toggleReplay = () => {
    const next = !replay.replaying;
    replay.setReplaying(next);
    router.replace(next ? ROUTES.onboarding : ROUTES.home);
  };

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        {compact ? (
          <button
            type="button"
            aria-label={translate("menu")}
            title={email ?? undefined}
            className="flex size-8 items-center justify-center rounded-full border border-border bg-background text-xs font-medium text-foreground transition-colors hover:bg-secondary"
          >
            {avatarLetter}
          </button>
        ) : (
          <button
            type="button"
            aria-label={translate("menu")}
            className="flex w-full items-center gap-2 rounded-md border border-border bg-background p-2 text-left text-xs transition-colors hover:bg-secondary/50"
          >
            <span className="flex size-7 shrink-0 items-center justify-center rounded-full bg-secondary font-medium text-foreground">
              {avatarLetter}
            </span>
            <span className="min-w-0 flex-1 truncate text-foreground">
              {email ?? translate("account")}
            </span>
            <RiArrowDownSLine
              className="size-4 shrink-0 text-muted-foreground"
              aria-hidden="true"
            />
          </button>
        )}
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" side={compact ? "right" : "top"}>
        {email ? <DropdownMenuLabel>{email}</DropdownMenuLabel> : null}
        <DropdownMenuSeparator />
        {replay.available ? (
          <DropdownMenuItem onSelect={toggleReplay}>
            <RiRestartLine />
            {replay.replaying ? translate("exitReplay") : translate("replayOnboarding")}
          </DropdownMenuItem>
        ) : null}
        <DropdownMenuItem onSelect={signOut} disabled={logout.isPending}>
          <RiLogoutBoxRLine />
          {logout.isPending ? translate("signingOut") : translate("signOut")}
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
