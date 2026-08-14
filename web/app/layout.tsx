import type { Metadata } from "next";
import localFont from "next/font/local";
import { NextIntlClientProvider } from "next-intl";
import { getLocale, getMessages, getTranslations } from "next-intl/server";
import type { ReactNode } from "react";
import { Providers } from "./providers";
import "./globals.css";

// The two brand webfonts, exposed as CSS variables the shared @condux/brand tokens bind to
// (--font-heading -> --font-chakra-petch, body -> --font-fira-code).
//
// next/font/LOCAL, not /google: the google loader downloads the files during the build, so an image
// build depended on fonts.googleapis.com answering. The files and their OFL licenses live in
// packages/brand/fonts, which the web image already copies (it builds from the repo root for the
// workspace deps), and the landing loads the same bytes. Runtime behaviour is unchanged: both loaders
// self-host and preload, so a browser never contacted Google either way.
const chakraPetch = localFont({
  src: [
    { path: "../../packages/brand/fonts/chakra-petch-500.woff2", weight: "500", style: "normal" },
    { path: "../../packages/brand/fonts/chakra-petch-600.woff2", weight: "600", style: "normal" },
    { path: "../../packages/brand/fonts/chakra-petch-700.woff2", weight: "700", style: "normal" },
  ],
  variable: "--font-chakra-petch",
  display: "swap",
});

// One file with a weight RANGE: Fira Code is a variable font, so Google serves the same bytes for 400
// and 500 and two static declarations would ship the identical 36KB twice.
const firaCode = localFont({
  src: "../../packages/brand/fonts/fira-code-variable.woff2",
  weight: "400 500",
  style: "normal",
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
      className={`${chakraPetch.variable} ${firaCode.variable}`}
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
