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

### In a server, scope one request at a time

Those calls are **process wide** by default, which is right for facts about the deployment and wrong for
facts about one request: Puma serves requests concurrently, so a bare `set_user` in a controller can
attach that user to a different request's error. That is worse than reporting no user, because it is
confidently wrong.

`Condux.request_scope` isolates it. Anything set inside belongs to that request alone, layered over the
process-wide values:

```ruby
Condux.request_scope do
  Condux.set_user({ "id" => current_user.id }) # this request only
  process(job)
end
```

**The Rack middleware below does this for you**, so a `set_user` in a Rails controller is already
isolated. Call it directly around background jobs, which have the same problem. It uses
`Thread.current[]`, which is fiber-local in Ruby, so it isolates Puma's threads and Falcon's fibers
alike.

Detail belonging to a single event can skip the scope entirely:

```ruby
Condux.capture_exception(error, request: { "url" => "/api/sync" }, tags: { "job" => "nightly" })
```

## Rack and Rails

```ruby
# config.ru
require "condux/rack"
use Condux::Rack::CaptureExceptions

# Rails (config/application.rb), the require at the top of the file
require "condux/rack"
config.middleware.use Condux::Rack::CaptureExceptions
```

Uncaught exceptions are reported as unhandled and re-raised, so the app's own error handling still runs.

**On Rails, caller-caused exceptions are re-raised but not reported.** Rails already classifies them:
`ActionDispatch::ExceptionWrapper.rescue_responses` maps an exception class to a status, and anything it
answers with a 4xx describes what the client sent rather than a defect in your app. A malformed JSON
body and a bad percent-encoded query both land there, and anyone can send those at will, so filing them
would let a stranger bury your real errors. Add your own with
`config.action_dispatch.rescue_responses`, which Condux reads too, so one setting governs your error
pages and your reporting together. Anything Rails does not classify still reports, since the registry
defaults to 500. Under bare Rack there is no such registry, so nothing is filtered.

**Pass the class, not its name as a string.** Rails builds each middleware with `klass.new(app)`, so a
string aborts boot with `undefined method 'new' for an instance of String`. Rails deprecated string
middleware in 5.0 and removed it in 5.1, so no supported version accepts it. The `require` matters too:
Bundler loads `condux`, which does not define `Condux::Rack`.

**On Rails use `config.middleware.use`, and nothing else.** `use` appends, which puts the middleware at
the bottom of the stack, inside `ActionDispatch::ShowExceptions`. That position is why it works:
`ShowExceptions` catches a controller exception and turns it into a 500, so anything above it never sees
the exception and reports nothing, with no error to tell you. `insert_before`, `insert_after` and
`unshift` all move it above and break it silently.

## Develop

```bash
for f in test/*.rb; do ruby -Ilib -Itest "$f"; done
```

Zero runtime dependencies (standard library only). The transport, sleep, and clock are injectable
(`Condux.init(... transport:, sleep:, clock:)`), so the tests exercise the retry/backoff with no real
network or timers.
