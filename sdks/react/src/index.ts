/**
 * Condux SDK for React — report errors to a Condux relay.
 *
 * Built on `@condux/browser` (init + automatic capture of uncaught errors / unhandled rejections), and
 * adds a `ConduxErrorBoundary` for **render errors** — React swallows those into the nearest error
 * boundary, so `window.onerror` never sees them; the boundary reports them as unhandled and shows an
 * optional fallback. Written with `createElement` (no JSX) so it stays a plain `.ts` module the tests run
 * under Node's type-stripping, matching the other JS SDKs.
 */

import { Component, type ErrorInfo, type ReactNode } from "react";
import { captureException } from "@condux/browser";

export * from "@condux/browser";

interface ErrorBoundaryProps {
  children?: ReactNode;
  /** Rendered instead of the children once a descendant render throws (defaults to nothing). */
  fallback?: ReactNode;
}

interface ErrorBoundaryState {
  hasError: boolean;
}

/**
 * A React error boundary that reports render errors to Condux (as **unhandled** — they escaped into
 * React's boundary machinery) and renders `fallback` in place of the broken subtree.
 *
 * ```tsx
 * <ConduxErrorBoundary fallback={<SomethingWentWrong />}>
 *   <App />
 * </ConduxErrorBoundary>
 * ```
 */
export class ConduxErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { hasError: false };

  static getDerivedStateFromError(): ErrorBoundaryState {
    return { hasError: true };
  }

  componentDidCatch(error: unknown, _info: ErrorInfo): void {
    void captureException(error, false);
  }

  render(): ReactNode {
    return this.state.hasError ? (this.props.fallback ?? null) : (this.props.children ?? null);
  }
}
