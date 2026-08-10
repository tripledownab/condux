# Condux SDK for Ruby

Report errors from a Ruby app to a Condux relay. Emits the Sentry "store" wire shape, so the relay
normalizes it exactly like an official Sentry SDK — point it at a project DSN and it works.

Delivery is resilient (429 / 5xx / network failures retry with backoff, honoring `Retry-After`) and
**never raises** — a failed send returns a `SendResult`, it does not crash the caller.

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

## Develop

```bash
ruby -Ilib -Itest test/test_condux.rb
```

Zero runtime dependencies (standard library only). The transport, sleep, and clock are injectable
(`Condux.init(... transport:, sleep:, clock:)`), so the tests exercise the retry/backoff with no real
network or timers.
