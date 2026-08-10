import type { Metadata } from "next";
import { Chakra_Petch, Fira_Code } from "next/font/google";
import { NextIntlClientProvider } from "next-intl";
import { getLocale, getMessages, getTranslations } from "next-intl/server";
import type { ReactNode } from "react";
import { Providers } from "./providers";
import "./globals.css";

// next/font self-hosts and optimizes the two brand webfonts, exposing each as a CSS variable that the
// shared @condux/brand tokens bind to (--font-heading -> --font-chakra-petch, body -> --font-fira-code).
const headingFont = Chakra_Petch({
  subsets: ["latin"],
  weight: ["500", "600", "700"],
  variable: "--font-chakra-petch",
  display: "swap",
});

const bodyFont = Fira_Code({
  subsets: ["latin"],
  weight: ["400", "500"],
  variable: "--font-fira-code",
  display: "swap",
});

export async function generateMetadata(): Promise<Metadata> {
  const translate = await getTranslations("app");
  return { title: translate("title"), description: translate("description") };
}

export default async function RootLayout({ children }: { children: ReactNode }) {
  // The active locale and its catalog come from the next-intl request config (i18n/request.ts). The
  // client provider hands the messages to every client component below.
  const locale = await getLocale();
  const messages = await getMessages();

  // suppressHydrationWarning: next-themes sets data-theme on <html> before hydration, so the server
  // and client markup differ by that attribute by design.
  return (
    <html
      lang={locale}
      suppressHydrationWarning
      className={`${headingFont.variable} ${bodyFont.variable}`}
    >
      {/* suppressHydrationWarning: browser extensions inject body attributes (data-atm-ext-*,
          data-gr-*) before React hydrates; attribute-level suppression keeps that noise out of the
          console without hiding real child mismatches. */}
      <body
        suppressHydrationWarning
        className="bg-background text-foreground font-sans antialiased"
      >
        <NextIntlClientProvider locale={locale} messages={messages}>
          <Providers>{children}</Providers>
        </NextIntlClientProvider>
      </body>
    </html>
  );
}
