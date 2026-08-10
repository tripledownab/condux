import { render } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { ReactElement } from "react";
import messages from "@/messages/en.json";

// Wraps a tree in the next-intl provider with the real English catalog, so components that call
// useTranslations render in tests exactly as they do under the root layout at runtime.
export function renderWithIntl(ui: ReactElement) {
  return render(
    <NextIntlClientProvider locale="en" messages={messages}>
      {ui}
    </NextIntlClientProvider>,
  );
}
