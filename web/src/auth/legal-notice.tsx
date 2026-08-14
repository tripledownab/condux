import { useTranslations } from "next-intl";
import { LegalDocument, legalUrl } from "@/src/lib/legal";

// The acceptance notice under the signup button. Small, and load-bearing: clause 1 of the terms says
// that creating an account accepts them, which is only true if the person was shown them at the time.
//
// Shown on signup only, not login. Acceptance happens once, when the account is created; repeating it
// at every sign-in would be noise, and it is the account creation that forms the contract.
//
// Renders nothing when the deployment publishes no terms (a self-hosted install). See src/lib/legal.ts.

export function LegalNotice() {
  const translate = useTranslations("auth");
  const termsUrl = legalUrl(LegalDocument.Terms);
  const privacyUrl = legalUrl(LegalDocument.Privacy);

  // Resolving the URLs IS the configured check, so there is one rule in one place. An earlier version
  // asked a separate hasPublishedTerms() here and then re-checked each URL inside the link, which made
  // the second check unreachable and left two copies of "is a legal site configured" free to drift.
  if (!termsUrl || !privacyUrl) {
    return null;
  }

  // Rich text rather than three concatenated strings, so a translator moves the links inside the
  // sentence instead of being handed fragments that only compose in English word order.
  return (
    <p className="mt-4 text-center text-xs text-muted-foreground">
      {translate.rich("legalNotice", {
        terms: (chunks) => <LegalLink href={termsUrl}>{chunks}</LegalLink>,
        privacy: (chunks) => <LegalLink href={privacyUrl}>{chunks}</LegalLink>,
      })}
    </p>
  );
}

// Plain anchor, not next/link: these leave the dashboard for the marketing site. A new tab keeps a
// half-filled signup form from being thrown away by reading the terms, which is the one thing that
// would make people not read them.
function LegalLink({ href, children }: { href: string; children: React.ReactNode }) {
  return (
    <a
      href={href}
      target="_blank"
      rel="noopener noreferrer"
      className="underline hover:text-foreground"
    >
      {children}
    </a>
  );
}
