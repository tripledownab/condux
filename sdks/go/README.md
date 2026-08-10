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

Hold one `*Client` for the process lifetime; it is safe for concurrent use.

Go errors carry no stack of their own, so `CaptureException` records the goroutine's stack **at capture
time** — call it close to the failure.

## Develop

```bash
gofmt -l .    # must print nothing
go vet ./...
go test ./...
```

Zero external dependencies (standard library only). The HTTP transport, sleep, and clock are injectable
(`Options.Transport` / `Sleep` / `Now`), so the tests exercise the retry/backoff with no real network or
timers.
