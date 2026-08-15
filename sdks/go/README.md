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
everything. The scope is safe for concurrent use.

### In a server, pass request detail per event

The scope is **process wide**, which is right for facts about the deployment and wrong for facts about
one request: a Go server handles requests on many goroutines at once, so a `SetUser` in a handler
attaches that user to whichever event is captured next, which may belong to a different request. That is
worse than reporting no user, because it is confidently wrong.

Pass anything request-specific at the capture call instead, where it cannot leak:

```go
func recoverMiddleware(client *condux.Client, next http.Handler) http.Handler {
    return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
        defer func() {
            if recovered := recover(); recovered != nil {
                client.CaptureUnhandledWith(fmt.Errorf("%v", recovered),
                    &condux.CaptureContext{Request: condux.RequestFrom(r)})
                panic(recovered) // the app's own recovery still runs
            }
        }()
        next.ServeHTTP(w, r)
    })
}
```

`CaptureExceptionWith` is the same for a handled error, and `CaptureContext.Tags` merge over the ambient
ones for that event only. `RequestFrom` reads the path, method and query string; headers are on the
request and deliberately not read, since they carry cookies and authorization and not sending
credentials is a stronger guarantee than scrubbing them later.

## Develop

```bash
gofmt -l .    # must print nothing
go vet ./...
go test ./...
```

Zero external dependencies (standard library only). The HTTP transport, sleep, and clock are injectable
(`Options.Transport` / `Sleep` / `Now`), so the tests exercise the retry/backoff with no real network or
timers.
