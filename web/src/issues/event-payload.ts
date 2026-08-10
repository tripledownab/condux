// Parses the stored event payload (the backend's normalized Event, PascalCase JSON) into the typed
// shape the detail view renders. Tolerant by design: a missing field stays undefined/empty and an
// unparsable payload returns null, so the card falls back to the raw JSON view instead of breaking.

export type ParsedFrame = {
  filename?: string;
  function?: string;
  lineno: number;
  inApp: boolean;
  contextLine?: string;
  contextBefore: string[];
  contextAfter: string[];
  vars: Record<string, string>;
};

export type ParsedException = {
  type?: string;
  value?: string;
  handled?: boolean;
  frames: ParsedFrame[];
};

export type ParsedBreadcrumb = {
  timestampUnixMs: number;
  category?: string;
  message?: string;
  level: number;
  data: Record<string, string>;
};

export type ParsedEvent = {
  exceptions: ParsedException[];
  breadcrumbs: ParsedBreadcrumb[];
  tags: Record<string, string>;
  contexts: Record<string, string>;
  modules: Record<string, string>;
  user?: { id?: string; username?: string };
  userKey: string;
  request?: { url?: string; method?: string };
  release?: string;
  environment?: string;
  serverName?: string;
  transaction?: string;
  dist?: string;
  traceId?: string;
  sdkName?: string;
  sdkVersion?: string;
};

// The newest exception is the one that crashed (mirrors the backend's primary-exception rule); its
// unhandled flag is the issue's "unhandled" badge.
export function primaryException(event: ParsedEvent): ParsedException | undefined {
  return event.exceptions.at(-1);
}

export function parseEventPayload(payload: string): ParsedEvent | null {
  let raw: unknown;
  try {
    raw = JSON.parse(payload);
  } catch {
    return null;
  }
  if (typeof raw !== "object" || raw === null) {
    return null;
  }

  // biome-ignore lint/suspicious/noExplicitAny: the payload is validated field by field below.
  const root = raw as any;
  return {
    exceptions: asArray(root.Exceptions).map((ex) => ({
      type: asString(ex?.Type),
      value: asString(ex?.Value),
      handled: typeof ex?.Handled === "boolean" ? ex.Handled : undefined,
      frames: asArray(ex?.Stacktrace?.Frames).map((frame) => ({
        filename: asString(frame?.Filename),
        function: asString(frame?.Function),
        lineno: typeof frame?.Lineno === "number" ? frame.Lineno : 0,
        inApp: frame?.InApp === true,
        contextLine: asString(frame?.ContextLine),
        contextBefore: asArray(frame?.ContextBefore).filter(
          (line): line is string => typeof line === "string",
        ),
        contextAfter: asArray(frame?.ContextAfter).filter(
          (line): line is string => typeof line === "string",
        ),
        vars: asStringMap(frame?.Vars),
      })),
    })),
    breadcrumbs: asArray(root.Breadcrumbs).map((crumb) => ({
      timestampUnixMs: typeof crumb?.TimestampUnixMs === "number" ? crumb.TimestampUnixMs : 0,
      category: asString(crumb?.Category),
      message: asString(crumb?.Message),
      level: typeof crumb?.Level === "number" ? crumb.Level : 0,
      data: asStringMap(crumb?.Data),
    })),
    tags: asStringMap(root.Tags),
    contexts: asStringMap(root.Contexts),
    modules: asStringMap(root.Modules),
    user:
      typeof root.User === "object" && root.User !== null
        ? { id: asString(root.User.Id), username: asString(root.User.Username) }
        : undefined,
    userKey: asString(root.UserKey) ?? "",
    request:
      typeof root.Request === "object" && root.Request !== null
        ? { url: asString(root.Request.Url), method: asString(root.Request.Method) }
        : undefined,
    release: asString(root.Release),
    environment: asString(root.Environment),
    serverName: asString(root.ServerName),
    transaction: asString(root.Transaction),
    dist: asString(root.Dist),
    traceId: asString(root.TraceId),
    sdkName: asString(root.SdkName),
    sdkVersion: asString(root.SdkVersion),
  };
}

function asString(value: unknown): string | undefined {
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

// biome-ignore lint/suspicious/noExplicitAny: each element is narrowed field by field at the read site.
function asArray(value: unknown): any[] {
  return Array.isArray(value) ? value : [];
}

function asStringMap(value: unknown): Record<string, string> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    return {};
  }
  const map: Record<string, string> = {};
  for (const [key, entry] of Object.entries(value)) {
    if (typeof entry === "string") {
      map[key] = entry;
    }
  }
  return map;
}

// Whether an event has anything for the evidence block (stack frames or breadcrumbs); callers skip
// the block entirely when there is nothing, so no empty spacing renders.
export function hasEvidence(event: ParsedEvent): boolean {
  const primary = primaryException(event);
  return (primary?.frames.length ?? 0) > 0 || event.breadcrumbs.length > 0;
}
