# Condux SDK for Ruby

Report errors from a Ruby app to a Condux relay. Emits the Sentry "store" wire shape, so the relay
normalizes it exactly like an official Sentry SDK — point it at a project DSN and it works.

Delivery is resilient (429 / 5xx / network failures retry with backoff, honoring `Retry-After`) and
**never raises** — a failed send returns a `SendResult`, it does not crash the caller. That holds even
if `init` was never called: capture warns once and drops the event, because the Rack middleware below
reports from inside a `rescue` and an SDK that raised there would replace your application's exception
with its own. A malformed DSN is refused by `init` instead, where it is a developer-time mistake.

## Usage

```ruby
require "condux"

Condux.init(
  dsn: "https://<key>@ingest.condux.ai/<projectId>",
  environment: "production",
  release: "1.4.2",
)

begin
  do_work
rescue => e
  Condux.capture_exception(e) # captures the exception's backtrace
  raise
end

# or a bare message
Condux.capture_message("cache miss storm", level: Condux::Level::WARNING)
```

## Verify your setup

Silence is what a broken error monitor and a healthy app look like from the outside, so prove the
pipeline once:

```bash
CONDUX_DSN="https://<key>@ingest.condux.ai/<projectId>" bundle exec condux test-event
```

Exit code 0 means delivered (the message appears as an info-level issue), 1 means delivery failed and
prints why, 2 means the DSN was missing or malformed.

## Enrichment

Attach the ambient facts triage always needs. Every subsequent event carries them, so nothing has to be
threaded through capture calls:

```ruby
Condux.set_user({ "id" => "1042", "email" => "dev@example.com" }) # nil clears it (sign-out)
Condux.set_tag("plan", "team")                                    # nil removes the tag
Condux.set_context("job", { "queue" => "billing", "attempt" => 3 })
Condux.add_breadcrumb("charge.started", category: "billing")
```

The breadcrumb trail keeps the most recent 30 entries. `Condux.clear_scope` resets everything.

## Rack and Rails

```ruby
# config.ru
require "condux/rack"
use Condux::Rack::CaptureExceptions

# Rails (config/application.rb)
config.middleware.use "Condux::Rack::CaptureExceptions"
```

Uncaught exceptions are reported as unhandled and re-raised, so the app's own error handling still runs.

## Develop

```bash
for f in test/*.rb; do ruby -Ilib -Itest "$f"; done
```

Zero runtime dependencies (standard library only). The transport, sleep, and clock are injectable
(`Condux.init(... transport:, sleep:, clock:)`), so the tests exercise the retry/backoff with no real
network or timers.
