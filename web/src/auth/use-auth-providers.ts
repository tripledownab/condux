import { useQuery } from "@tanstack/react-query";
import { conduxFetch } from "@/src/api/fetcher";

type ProvidersResponse = {
  status: number;
  data: { google: boolean; sso: boolean };
  headers: Headers;
};

// Which external sign-in options the control-plane has configured: `google` (CONDUX_GOOGLE_*) and `sso`
// (enterprise per-org OIDC, gated on CONDUX_SECRET_KEY). Cached for the session since it is env-driven and
// does not change while the app runs; a fetch failure resolves to all-false, so the buttons stay hidden
// rather than erroring the auth page.
export function useAuthProviders(): { google: boolean; sso: boolean } {
  const query = useQuery({
    queryKey: ["auth", "providers"],
    queryFn: () => conduxFetch<ProvidersResponse>("/api/auth/providers"),
    staleTime: Number.POSITIVE_INFINITY,
    retry: false,
  });
  return { google: query.data?.data.google ?? false, sso: query.data?.data.sso ?? false };
}
