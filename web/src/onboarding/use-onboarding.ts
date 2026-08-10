import { useMe } from "@/src/api/generated/condux";
import { useReplayOnboarding } from "./replay-onboarding";

// Whether the user has finished first-run onboarding. It is an explicit, server-recorded signal
// (`users.onboarded_at`, exposed as `me.onboarded`): a fresh owner is done when they click Finish, an
// invited user is marked done on accepting the invite, and existing users were grandfathered by the
// migration. Completion can't be derived from data (a fresh owner has an org + a project the moment
// before Finish just as after), which is why it is stored. The dev replay flag forces "not done" to
// re-walk the flow.
export interface OnboardingState {
  isPending: boolean;
  isComplete: boolean;
}

export function useOnboarding(): OnboardingState {
  const { replaying } = useReplayOnboarding();
  const me = useMe();

  if (replaying) {
    return { isPending: false, isComplete: false };
  }
  if (me.isPending) {
    return { isPending: true, isComplete: false };
  }
  // A failed /me is the auth guard's concern (a 401 redirects to login), not this gate's — never trap a
  // user in onboarding on an error.
  if (me.isError) {
    return { isPending: false, isComplete: true };
  }
  return { isPending: false, isComplete: me.data?.data.onboarded ?? false };
}
