# Condux SDK for Go

Report errors from a Go app to a Condux relay. Emits the Sentry "store" wire shape, so the relay
normalizes it exactly like an official Sentry SDK — point it at a project DSN and it works.

Delivery is resilient (429 / 5xx / network failures retry with backoff, honoring `Retry-After`) and
**never panics** — a failed send returns a `SendResult`, it does not crash the caller.

## Install

```bash
go get github.com/tripledownab/condux/sdks/go
```

## Usage

```go
package main

import "github.com/tripledownab/condux/sdks/go"

func main() {
	client, err := condux.New(condux.Options{
		DSN:         "https://<key>@ingest.condux.ai/<projectID>",
		Environment: "production",
		Release:     "1.4.2",
	})
	if err != nil {
		panic(err) // a malformed DSN is a startup config error
	}

	if err := doWork(); err != nil {
		client.CaptureException(err) // captures the current goroutine's stack
	}

	client.CaptureMessage("cache miss storm", condux.LevelWarning)
}
```

Hold one `*Client` for the process lifetime; it is safe for concurrent use. Capture on a nil `*Client`
warns once and drops the event rather than panicking, so a missed `New` error cannot crash the app in the
path where it is already handling a failure.

Go errors carry no stack of their own, so `CaptureException` records the goroutine's stack **at capture
time** — call it close to the failure.

## Handled and unhandled

`CaptureException` reports a handled error. Use `CaptureUnhandled` for one that already escaped — a
recovered panic, or an error that reached your HTTP error path — so the issue gets the unhandled badge:

```go
defer func() {
	if recovered := recover(); recovered != nil {
		client.CaptureUnhandled(fmt.Errorf("panic: %v", recovered))
		panic(recovered) // report, then let it continue unwinding
	}
}()
```

## Verify your setup

Silence is what a broken error monitor and a healthy app look like from the outside, so prove the
pipeline once:

```bash
CONDUX_DSN="https://<key>@ingest.condux.ai/<projectID>" \
  go run github.com/tripledownab/condux/sdks/go/cmd/condux-test-event
```

Exit code 0 means delivered (the message appears as an info-level issue), 1 means delivery failed and
prints why, 2 means the DSN was missing or malformed.

## Enrichment

Attach the ambient facts triage always needs. Every subsequent event carries them, so nothing has to be
threaded through capture calls:

```go
condux.SetUser(&condux.User{ID: "1042", Email: "dev@example.com"}) // nil clears it (sign-out)
condux.SetTag("plan", "team")                                      // condux.RemoveTag drops it
condux.SetContext("job", map[string]any{"queue": "billing"})       // nil removes the context
condux.AddBreadcrumb(condux.Breadcrumb{Message: "charge.started", Category: "billing"})
```

The trail keeps the most recent `condux.MaxBreadcrumbs` (30) entries. `condux.ClearScope()` resets
everything. The scope is process wide and safe for concurrent use.

## Develop

```bash
gofmt -l .    # must print nothing
go vet ./...
go test ./...
```

Zero external dependencies (standard library only). The HTTP transport, sleep, and clock are injectable
(`Options.Transport` / `Sleep` / `Now`), so the tests exercise the retry/backoff with no real network or
timers.
