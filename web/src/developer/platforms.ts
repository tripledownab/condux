// The SDK setup catalog, kept as data so the Developer guide stays declarative. One entry per shipped
// SDK; `code(dsn)` interpolates the project's real DSN so every snippet is copy-paste ready. `titleKey`
// is translated under "developer.snippets" (install / initialize / framework, where the framework label
// takes the adapter's name). Platform + framework display names are proper nouns, kept here with the code.
// Extend this array to add a platform; nothing else changes.

export type SnippetTitle = "install" | "initialize" | "framework" | "sourcemaps";

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
        code: () => `# In CI after \`next build\`: upload client source maps so browser stack traces de-minify.
# Mint a release token in the project's GitHub tab, then use the same release your app reports:
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
];

// The platform whose snippets to show first: the project's own platform when we ship an SDK for it,
// else the first in the catalog. Pure so the guide stays declarative wiring.
export function defaultPlatform(projectPlatform: string | undefined): Platform {
  const match = projectPlatform
    ? PLATFORMS.find((platform) => platform.id === projectPlatform.toLowerCase())
    : undefined;
  return match ?? PLATFORMS[0];
}
