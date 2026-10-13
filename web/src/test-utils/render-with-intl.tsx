import { render } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { ReactElement, ReactNode } from "react";
import messages from "@/messages/en.json";

// Wraps a tree in the next-intl provider with the real English catalog, so components that call
// useTranslations render in tests exactly as they do under the root layout at runtime. It is passed as
// the wrapper rather than around the tree, so a test's rerender keeps the provider too.
function IntlProvider({ children }: { children: ReactNode }) {
  return (
    <NextIntlClientProvider locale="en" messages={messages}>
      {children}
    </NextIntlClientProvider>
  );
}

export function renderWithIntl(ui: ReactElement) {
  return render(ui, { wrapper: IntlProvider });
}
