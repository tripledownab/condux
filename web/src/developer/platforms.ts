// The SDK setup catalog, kept as data so the Developer guide stays declarative. One entry per platform
// we cover, with our own SDK or a stock Sentry one; `code(dsn)` interpolates the project's real DSN so every snippet is copy-paste ready. `titleKey`
// is translated under "developer.snippets" (install / initialize / framework, where the framework label
// takes the adapter's name). Platform + framework display names are proper nouns, kept here with the code.
// Extend this array to add a platform; nothing else changes.

import { toOtlpTarget } from "@/src/lib/dsn";

// What the guide passes to `code(dsn)` before the project has an active key. Every snippet must render it.
export const DSN_PLACEHOLDER = "<your-dsn>";

export type SnippetTitle =
  | "install"
  | "initialize"
  | "framework"
  | "sourcemaps"
  | "build"
  | "symbols";

export type Snippet = {
  titleKey: SnippetTitle;
  framework?: string;
  language: string;
  code: (dsn: string) => string;
};

export type Platform = {
  id: string;
  name: string;
  snippets: Snippet[];
};

export const PLATFORMS: Platform[] = [
  {
    id: "javascript",
    name: "JavaScript (Node.js)",
    snippets: [
      { titleKey: "install", language: "bash", code: () => "npm install @condux/node" },
      {
        titleKey: "initialize",
        language: "typescript",
        code: (dsn) => `import { init, captureException } from "@condux/node";

init({ dsn: "${dsn}", environment: "production", release: "app@1.0.0" });

try {
  doWork();
} catch (error) {
  captureException(error);
  throw error;
}`,
      },
      {
        titleKey: "framework",
        framework: "Express",
        language: "typescript",
        code: () => `// npm install @condux/express
import { conduxErrorHandler } from "@condux/express";

// Register after your routes: reports anything reaching Express's error pipeline, then passes it on.
app.use(conduxErrorHandler());`,
      },
    ],
  },
  {
    id: "browser",
    name: "Browser",
    snippets: [
      { titleKey: "install", language: "bash", code: () => "npm install @condux/browser" },
      {
        titleKey: "initialize",
        language: "typescript",
        code: (dsn) => `import { init } from "@condux/browser";

// Uncaught errors + unhandled rejections are reported automatically.
init({ dsn: "${dsn}", environment: "production", release: "app@1.0.0" });`,
      },
      {
        titleKey: "framework",
        framework: "React",
        language: "tsx",
        code: (dsn) => `// npm install @condux/react
import { init, ConduxErrorBoundary } from "@condux/react";

init({ dsn: "${dsn}" });

// Catches render errors window.onerror never sees:
<ConduxErrorBoundary fallback={<SomethingWentWrong />}>
  <App />
</ConduxErrorBoundary>;`,
      },
    ],
  },
  {
    id: "nextjs",
    name: "Next.js",
    snippets: [
      { titleKey: "install", language: "bash", code: () => "npm install @condux/nextjs" },
      {
        titleKey: "initialize",
        language: "dotenv",
        code: (dsn) => `# .env (the DSN is read from the environment)
CONDUX_DSN=${dsn}              # server + edge (kept server-only)
NEXT_PUBLIC_CONDUX_DSN=${dsn}  # client (exposed to the browser)`,
      },
      {
        titleKey: "framework",
        framework: "App Router",
        language: "typescript",
        code: () => `// instrumentation.ts
export { register, captureRequestError as onRequestError } from "@condux/nextjs";

// instrumentation-client.ts
import { initClient } from "@condux/nextjs";
initClient();`,
      },
      {
        titleKey: "sourcemaps",
        language: "bash",
        code: () => `# In CI after \`next build\`, before packaging: upload source maps so stack traces de-minify.
# Mint a release token in the project's Releases tab, then use the same release your app reports:
CONDUX_RELEASE_TOKEN=condux_rel_... \\
  npx condux-sourcemaps --url https://app.condux.ai --release "$(git rev-parse HEAD)"`,
      },
    ],
  },
  {
    id: "edge",
    name: "Edge (Cloudflare / Vercel)",
    snippets: [
      { titleKey: "install", language: "bash", code: () => "npm install @condux/edge" },
      {
        titleKey: "initialize",
        language: "typescript",
        code: (dsn) => `import { init, wrapFetch } from "@condux/edge";

init({ dsn: "${dsn}", environment: "production", release: "worker@1.0.0" });

// wrapFetch reports anything the handler throws (as unhandled) and rethrows.
export default {
  fetch: wrapFetch(async (request: Request) => handle(request)),
};`,
      },
    ],
  },
  {
    id: "python",
    name: "Python",
    snippets: [
      { titleKey: "install", language: "bash", code: () => "pip install condux" },
      {
        titleKey: "initialize",
        language: "python",
        code: (dsn) => `import condux

condux.init(dsn="${dsn}", environment="production", release="app@1.0.0")

try:
    do_work()
except Exception as error:
    condux.capture_exception(error)
    raise`,
      },
      {
        titleKey: "framework",
        framework: "FastAPI / Flask / Django",
        language: "python",
        code: () => `# FastAPI / Starlette
from condux.integrations.asgi import ConduxAsgiMiddleware
app.add_middleware(ConduxAsgiMiddleware)

# Flask / Django (WSGI)
from condux.integrations.wsgi import ConduxWsgiMiddleware
app.wsgi_app = ConduxWsgiMiddleware(app.wsgi_app)`,
      },
    ],
  },
  {
    id: "go",
    name: "Go",
    snippets: [
      {
        titleKey: "install",
        language: "bash",
        code: () => "go get github.com/tripledownab/condux/sdks/go",
      },
      {
        titleKey: "initialize",
        language: "go",
        code: (dsn) => `import "github.com/tripledownab/condux/sdks/go"

client, err := condux.New(condux.Options{
    DSN:         "${dsn}",
    Environment: "production",
    Release:     "1.0.0",
})
if err != nil {
    panic(err) // a malformed DSN is a startup config error
}

if err := doWork(); err != nil {
    client.CaptureException(err) // records the goroutine's stack at capture time
}`,
      },
    ],
  },
  {
    id: "ruby",
    name: "Ruby",
    snippets: [
      { titleKey: "install", language: "bash", code: () => "bundle add condux" },
      {
        titleKey: "initialize",
        language: "ruby",
        code: (dsn) => `require "condux"

Condux.init(dsn: "${dsn}", environment: "production", release: "1.0.0")

begin
  do_work
rescue => e
  Condux.capture_exception(e)
  raise
end`,
      },
      {
        titleKey: "framework",
        framework: "Rails / Rack",
        language: "ruby",
        code: () => `# config/application.rb (Rails), or config.ru for any Rack app
config.middleware.use Condux::Rack::CaptureExceptions`,
      },
    ],
  },
  {
    id: "php",
    name: "PHP",
    snippets: [
      { titleKey: "install", language: "bash", code: () => "composer require condux/condux" },
      {
        titleKey: "initialize",
        language: "php",
        code: (dsn) => `use Condux\\Client;

$condux = new Client(dsn: '${dsn}', environment: 'production', release: '1.0.0');

try {
    do_work();
} catch (\\Throwable $e) {
    $condux->captureException($e);
    throw $e;
}`,
      },
      {
        titleKey: "framework",
        framework: "Laravel",
        language: "dotenv",
        code: (
          dsn,
        ) => `# composer require condux/condux-laravel (the service provider auto-discovers).
# Set the DSN and uncaught exceptions report automatically (no code changes):
CONDUX_DSN=${dsn}`,
      },
    ],
  },
  {
    id: "jvm",
    name: "Java / Kotlin (JVM)",
    snippets: [
      {
        titleKey: "install",
        language: "kotlin",
        code: () => `// Gradle
implementation("ai.condux:condux:0.1.0")`,
      },
      {
        titleKey: "initialize",
        language: "java",
        code: (dsn) => `import ai.condux.ConduxClient;

ConduxClient condux = ConduxClient.builder("${dsn}")
    .environment("production")
    .release("1.0.0")
    .build();

try {
    doWork();
} catch (Exception e) {
    condux.captureException(e);
    throw e;
}`,
      },
      {
        titleKey: "framework",
        framework: "Spring Boot",
        language: "java",
        code: () => `// Register the servlet filter as a bean (Spring Boot MVC + any servlet app):
@Bean
FilterRegistrationBean<ConduxExceptionFilter> conduxFilter(ConduxClient client) {
    var reg = new FilterRegistrationBean<>(new ConduxExceptionFilter(client));
    reg.setOrder(Ordered.HIGHEST_PRECEDENCE);
    return reg;
}`,
      },
    ],
  },
  {
    id: "dotnet",
    name: ".NET (C#)",
    snippets: [
      { titleKey: "install", language: "bash", code: () => "dotnet add package Condux.Sdk" },
      {
        titleKey: "initialize",
        language: "csharp",
        code: (dsn) => `using Condux.Sdk;

var condux = new ConduxClient(new ConduxOptions
{
    Dsn = "${dsn}",
    Environment = "production",
    Release = "1.0.0",
});

try
{
    DoWork();
}
catch (Exception error)
{
    await condux.CaptureExceptionAsync(error);
    throw;
}`,
      },
      {
        titleKey: "framework",
        framework: "ASP.NET Core",
        language: "csharp",
        code: (dsn) => `// dotnet add package Condux.Sdk.AspNetCore
builder.Services.AddSingleton(new ConduxClient(new ConduxOptions { Dsn = "${dsn}" }));

var app = builder.Build();
app.UseConduxExceptionReporting(); // register early, before UseRouting`,
      },
    ],
  },
  {
    id: "flutter",
    name: "Flutter",
    snippets: [
      { titleKey: "install", language: "bash", code: () => "flutter pub add sentry_flutter" },
      {
        titleKey: "initialize",
        language: "dart",
        code: (
          dsn,
        ) => `// lib/main.dart: the stock sentry_flutter SDK reports to Condux; only the DSN changes.
import 'package:flutter/material.dart';
import 'package:sentry_flutter/sentry_flutter.dart';

Future<void> main() async {
  await SentryFlutter.init(
    (options) => options.dsn = '${dsn}',
    appRunner: () => runApp(const MyApp()),
  );
}`,
      },
      {
        titleKey: "build",
        language: "bash",
        code: () => `# An obfuscated release reports bare addresses and renamed classes. Split the debug info out so
# Condux can name the addresses, save the obfuscation map so it can restore the class names in an
# exception's type and message, and upload both for every build: each matches only the build that wrote it.
flutter build appbundle --release --obfuscate --split-debug-info=build/symbols \\
  --extra-gen-snapshot-options=--save-obfuscation-map=build/symbols/obfuscation-map-android.json
flutter build ipa --release --obfuscate --split-debug-info=build/symbols \\
  --extra-gen-snapshot-options=--save-obfuscation-map=build/symbols/obfuscation-map-ios.json`,
      },
      {
        titleKey: "symbols",
        language: "yaml",
        code: () => `# GitHub Actions, after the build step. Mint a release token in the project's Releases tab and
# store it as the CONDUX_RELEASE_TOKEN secret. One request per file; a busy server answers 429,
# which curl's --retry waits out. Android: its .symbols files. iOS: the DWARF file inside the app's
# dSYM, which names the crash's build; the .symbols file an iOS build writes does not, so it is skipped.
# Each platform's obfuscation map is uploaded once for each build its debug files hold, named by the
# debugId each upload answered; jq reads it.
- name: Upload debug symbols to Condux
  env:
    CONDUX_RELEASE_TOKEN: \${{ secrets.CONDUX_RELEASE_TOKEN }}
  run: |
    set -euo pipefail
    uploaded=0
    rm -f build/symbols/maps-uploaded
    upload() {  # a debug file, and the obfuscation map its build saved
      answer=$(curl --fail --silent --show-error --retry 5 \\
        -H "Authorization: Bearer $CONDUX_RELEASE_TOKEN" \\
        --data-binary "@$1" https://app.condux.ai/api/debug-files)
      uploaded=$((uploaded + 1))
      [ -f "$2" ] || return 0
      ids=$(printf '%s' "$answer" | jq -r '.files[].debugId')
      for id in $ids; do
        curl --fail --silent --show-error --retry 5 \\
          -H "Authorization: Bearer $CONDUX_RELEASE_TOKEN" -H "Content-Type: application/json" \\
          --data-binary "@$2" "https://app.condux.ai/api/dart-symbol-maps?debugId=$id"
        echo "$2" >> build/symbols/maps-uploaded
      done
    }
    for file in build/symbols/app.android-*.symbols; do
      [ -f "$file" ] || continue
      upload "$file" build/symbols/obfuscation-map-android.json
    done
    for file in $(find build/ios -path '*App.framework.dSYM/Contents/Resources/DWARF/App' 2>/dev/null); do
      upload "$file" build/symbols/obfuscation-map-ios.json
    done
    # A build that wrote no debug file, or a saved map that went nowhere, would otherwise pass here and
    # leave every crash unreadable.
    [ "$uploaded" -gt 0 ] || { echo "no debug files found under build/"; exit 1; }
    for map in build/symbols/obfuscation-map-*.json; do
      [ -f "$map" ] || continue
      grep -qxF "$map" build/symbols/maps-uploaded 2>/dev/null || { echo "$map was saved but not uploaded"; exit 1; }
    done`,
      },
    ],
  },
  {
    id: "opentelemetry",
    name: "OpenTelemetry (AI agents)",
    snippets: [
      {
        titleKey: "install",
        language: "bash",
        code: () =>
          "pip install opentelemetry-sdk opentelemetry-exporter-otlp-proto-http opentelemetry-instrumentation-openai-agents-v2",
      },
      {
        titleKey: "initialize",
        language: "python",
        code: (dsn) => {
          // Before the project has a key the guide passes a placeholder, which names no endpoint.
          const { endpoint, key } = toOtlpTarget(dsn) ?? {
            endpoint: "<your-ingest-url>/api/<project-id>",
            key: "<your-key>",
          };
          return `from opentelemetry import trace
from opentelemetry.exporter.otlp.proto.http.trace_exporter import OTLPSpanExporter
from opentelemetry.instrumentation.openai_agents import OpenAIAgentsInstrumentor
from opentelemetry.sdk.resources import Resource
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import BatchSpanProcessor

# Traces go to Condux, which turns failed model calls, tools and agent runs into issues and drops the
# prompts and tool arguments the spans carry. The endpoint and key come from your DSN: ${dsn}
provider = TracerProvider(resource=Resource.create({"service.name": "my-agent", "service.version": "1.0.0"}))
provider.add_span_processor(BatchSpanProcessor(OTLPSpanExporter(
    endpoint="${endpoint}/v1/traces",
    headers={"x-condux-auth": "${key}"},
)))
trace.set_tracer_provider(provider)

OpenAIAgentsInstrumentor().instrument()`;
        },
      },
    ],
  },
];

// The platform whose snippets to show first: the project's own platform when we ship an SDK for it,
// else the first in the catalog. Pure so the guide stays declarative wiring.
export function defaultPlatform(projectPlatform: string | undefined): Platform {
  const match = projectPlatform
    ? PLATFORMS.find((platform) => platform.id === projectPlatform.toLowerCase())
    : undefined;
  return match ?? PLATFORMS[0];
}
