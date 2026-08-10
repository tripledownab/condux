"use client";

import { useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { useEffect, useState } from "react";
import type { ConduxApiError } from "@/src/api/fetcher";
import {
  getListMyOrgsQueryKey,
  getMeQueryKey,
  useAcceptInvite,
  useLogout,
  useMe,
} from "@/src/api/generated/condux";
import { PRIMARY_BUTTON_CLASS, SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { Notice } from "@/src/components/notice";
import { ROUTES } from "@/src/routes";

// Map an acceptInvite failure to a message key. 403 = signed in as the wrong email; 404 = the token is
// invalid or expired; 409 carries a code (already a member of this org, or already in a different org).
function acceptErrorKey(error: unknown): string {
  const api = error as ConduxApiError | null;
  if (api?.status === 403) {
    return "wrongEmail";
  }
  if (api?.status === 404) {
    return "invalid";
  }
  if (api?.status === 409) {
    return api.code === "already_member" ? "alreadyMember" : "alreadyInOrg";
  }
  return "generic";
}

// The invite acceptance page (#84 follow-up). The invitee opens this from the emailed link (/invite?token).
// It lives outside the app shell so a logged-in user with no org is not bounced to onboarding by the gate,
// and a logged-out invitee can be prompted to sign up first. Accepting joins the inviting org and lands
// on the dashboard.
export default function InvitePage() {
  const translate = useTranslations("invite");
  const router = useRouter();
  const queryClient = useQueryClient();
  const me = useMe({ query: { retry: false } });
  const accept = useAcceptInvite<ConduxApiError>();
  const logout = useLogout();

  // Read the token from the URL on the client (not useSearchParams, which would force a Suspense
  // boundary for prerender — matching the auth form's approach).
  const [token, setToken] = useState<string | null>(null);
  useEffect(() => {
    setToken(new URLSearchParams(window.location.search).get("token"));
  }, []);

  const authTarget = token ? `/invite?token=${encodeURIComponent(token)}` : ROUTES.home;
  const authQuery = `?next=${encodeURIComponent(authTarget)}`;

  const submit = () => {
    if (!token) {
      return;
    }
    accept.mutate(
      { data: { token } },
      {
        onSuccess: async () => {
          // A joined org changes both "me" and the org list the onboarding gate reads; refresh before
          // landing on the dashboard so the gate sees the new membership.
          await Promise.all([
            queryClient.invalidateQueries({ queryKey: getMeQueryKey() }),
            queryClient.invalidateQueries({ queryKey: getListMyOrgsQueryKey() }),
          ]);
          router.replace(ROUTES.home);
        },
      },
    );
  };

  const card = (children: React.ReactNode) => (
    <div className="rounded-lg border border-border bg-card p-6">
      <h1 className="font-heading text-xl font-semibold text-foreground">{translate("title")}</h1>
      <div className="mt-4 flex flex-col gap-4">{children}</div>
    </div>
  );

  if (
    token === null &&
    typeof window !== "undefined" &&
    !window.location.search.includes("token")
  ) {
    return card(<Notice>{translate("missingToken")}</Notice>);
  }

  if (me.isPending) {
    return card(<p className="text-sm text-muted-foreground">{translate("loading")}</p>);
  }

  // Signed out (the me query 401s): prompt to sign in or create an account, carrying the invite token
  // back through the auth flow so the user returns here to accept.
  if (me.isError) {
    return card(
      <>
        <p className="text-sm text-muted-foreground">{translate("signedOutPrompt")}</p>
        <div className="flex flex-wrap gap-2">
          <Link href={`${ROUTES.signup}${authQuery}`} className={PRIMARY_BUTTON_CLASS}>
            {translate("createAccount")}
          </Link>
          <Link href={`${ROUTES.login}${authQuery}`} className={SECONDARY_BUTTON_CLASS}>
            {translate("signIn")}
          </Link>
        </div>
      </>,
    );
  }

  const email = me.data?.data.email;
  const accepted = accept.isSuccess;
  const errorKey = accept.isError ? acceptErrorKey(accept.error) : null;

  return card(
    <>
      <p className="text-sm text-muted-foreground">
        {translate("signedInAs", { email: email ?? "" })}
      </p>
      {accepted ? (
        <p className="text-sm text-foreground">{translate("success")}</p>
      ) : (
        <>
          <button
            type="button"
            onClick={submit}
            disabled={accept.isPending || !token}
            className={PRIMARY_BUTTON_CLASS}
          >
            {accept.isPending ? translate("accepting") : translate("accept")}
          </button>
          {errorKey ? (
            <div className="flex flex-col gap-2">
              <p role="alert" className="text-sm text-error">
                {translate(`errors.${errorKey}`)}
              </p>
              {errorKey === "wrongEmail" ? (
                <button
                  type="button"
                  onClick={() =>
                    logout.mutate(undefined, {
                      onSuccess: () => queryClient.invalidateQueries({ queryKey: getMeQueryKey() }),
                    })
                  }
                  className={SECONDARY_BUTTON_CLASS}
                >
                  {translate("useDifferentAccount")}
                </button>
              ) : null}
            </div>
          ) : null}
        </>
      )}
    </>,
  );
}
