import { useTranslations } from "next-intl";
import { Loader } from "./loader";

// A full-height centered loading state, used while the auth guard resolves the session. The animated
// Condux wordmark stands in for a spinner; role=status + the visually-hidden label announce it to
// assistive tech (the mark itself is decorative).
export function FullScreenLoading() {
  const translate = useTranslations("common");
  return (
    <div
      className="flex min-h-screen items-center justify-center bg-background"
      role="status"
      aria-live="polite"
    >
      <Loader className="h-auto w-40 text-muted-foreground" />
      <span className="sr-only">{translate("loading")}</span>
    </div>
  );
}
