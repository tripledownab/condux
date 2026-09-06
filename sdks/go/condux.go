// Package condux reports errors to a Condux relay.
//
// It emits the Sentry "store" wire shape (event_id, timestamp, level, exception.values[]) so the relay
// normalizes it exactly like an official Sentry SDK: point it at a project DSN and it works. Delivery is
// resilient (429 / 5xx / network failures retry with backoff, honoring Retry-After) and never panics — a
// failed send returns a SendResult. The HTTP transport, sleep, and clock are injectable so backoff is
// exercised with no real network or timers. Inspired by common SDK transports, implemented fresh.
package condux

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"net/http"
	"net/url"
	"os"
	"strings"
	"sync"
	"time"
)

var warnOnce sync.Once

func warnUninitialized() {
	warnOnce.Do(func() {
		_, _ = os.Stderr.WriteString("condux: capture called on a nil Client; events are being dropped.\n")
	})
}

// Options configures a Client.
type Options struct {
	// DSN is the project DSN, e.g. https://<key>@ingest.condux.ai/<projectID>.
	DSN string
	// Environment tags the deployment (e.g. "production"). Optional.
	Environment string
	// Release identifies the build (e.g. a version or commit). Optional.
	Release string
	// MaxRetries is the number of extra delivery attempts after the first (0 means the default of 3).
	MaxRetries int
	// Transport, Sleep, and Now are test/advanced hooks; the zero values use the real HTTP client,
	// time.Sleep, and time.Now.
	Transport http.RoundTripper
	Sleep     func(time.Duration)
	Now       func() time.Time
	// DisableModules turns off the runtime dependency inventory (ADR-0041), which otherwise reports the
	// module versions built into this binary so a security advisory can be answered with the version
	// actually running. Off by default because the inventory is worth having; set it when the payload
	// cost matters more than the answer.
	DisableModules bool
}

// Client reports errors to a Condux relay. One Client is cheap to hold for the process lifetime and is
// safe for concurrent use.
type Client struct {
	opts       Options
	http       *http.Client
	storeURL   string
	publicKey  string
	maxRetries int
	sleep      func(time.Duration)
	now        func() time.Time
}

const defaultMaxRetries = 3

// New builds a Client from opts. It returns an error only for a malformed DSN — a config problem that
// should fail loud at startup rather than be swallowed on every capture.
func New(opts Options) (*Client, error) {
	endpoint, projectID, publicKey, err := parseDSN(opts.DSN)
	if err != nil {
		return nil, err
	}

	maxRetries := opts.MaxRetries
	if maxRetries == 0 {
		maxRetries = defaultMaxRetries
	}
	sleep := opts.Sleep
	if sleep == nil {
		sleep = time.Sleep
	}
	now := opts.Now
	if now == nil {
		now = time.Now
	}

	// The runtime dependency inventory (ADR-0041); see initModules for why it is read here.
	initModules(opts.DisableModules)

	return &Client{
		opts:       opts,
		http:       &http.Client{Transport: opts.Transport},
		storeURL:   endpoint + "/api/" + projectID + "/store/",
		publicKey:  publicKey,
		maxRetries: maxRetries,
		sleep:      sleep,
		now:        now,
	}, nil
}

// CaptureException reports err as an error-level event, capturing the current goroutine's stack. This is
// a handled capture; use CaptureUnhandled for an error that already escaped (a recovered panic, or one
// that reached an HTTP handler's error path) so the relay marks the issue unhandled.
func (c *Client) CaptureException(err error) SendResult {
	return c.dispatch(event{Level: LevelError, Exception: toException(err, true)})
}

// CaptureUnhandled reports err as an unhandled error-level event. Call it from a recover() or an error
// middleware: mechanism.handled rides the wire as false, which is what drives the unhandled badge.
func (c *Client) CaptureUnhandled(err error) SendResult {
	return c.dispatch(event{Level: LevelError, Exception: toException(err, false)})
}

// CaptureUnhandledWith reports an unhandled error together with detail belonging to this one event, such
// as the request it happened during. Use it from HTTP middleware:
//
//	defer func() {
//		if recovered := recover(); recovered != nil {
//			client.CaptureUnhandledWith(fmt.Errorf("%v", recovered),
//				&condux.CaptureContext{Request: condux.RequestFrom(r)})
//			panic(recovered)
//		}
//	}()
//
// The request is passed here rather than set with SetTag because the package scope is global and a
// server handles many requests at once, so an ambient value attaches to whichever event is captured
// next. A nil context reports exactly as CaptureUnhandled does.
func (c *Client) CaptureUnhandledWith(err error, ctx *CaptureContext) SendResult {
	return c.dispatch(applyCaptureContext(event{Level: LevelError, Exception: toException(err, false)}, ctx))
}

// CaptureExceptionWith reports a handled error with per-event detail. See CaptureUnhandledWith.
func (c *Client) CaptureExceptionWith(err error, ctx *CaptureContext) SendResult {
	return c.dispatch(applyCaptureContext(event{Level: LevelError, Exception: toException(err, true)}, ctx))
}

// applyCaptureContext puts the per-event detail on the event. Tags are merged in dispatch, after the
// ambient scope has been applied, so this only records them.
func applyCaptureContext(e event, ctx *CaptureContext) event {
	if ctx == nil {
		return e
	}
	e.Request = ctx.Request
	e.Tags = ctx.Tags
	return e
}

// CaptureMessage reports a bare message event at the given level.
func (c *Client) CaptureMessage(message string, level Level) SendResult {
	return c.dispatch(event{Level: level, Message: message})
}

func (c *Client) dispatch(e event) SendResult {
	// Reporting never panics — an error monitor that panics turns a handled error into a crash in exactly
	// the code path where someone is already dealing with a failure. A nil Client (New returned an error
	// and the result was kept anyway) warns once and drops the event.
	if c == nil {
		warnUninitialized()
		return SendResult{OK: false, Error: "not_initialized"}
	}

	e.EventID = newEventID()
	// Read the clock once. It is a caller-supplied hook, so calling it twice per event would advance a
	// test clock that returns a sequence, and would date the event and the inventory's interval from two
	// different instants.
	now := c.now()
	e.Timestamp = float64(now.UnixNano()) / float64(time.Second) // epoch seconds, the store convention
	e.Platform = "go"
	e.Environment = c.opts.Environment
	e.Release = c.opts.Release
	if !c.opts.DisableModules {
		// The runtime dependency inventory (ADR-0041), attached on the first event and then only once
		// per interval; nil the rest of the time, so the key is absent rather than empty.
		e.Modules = modulesField(now)
	}

	// Per-event tags are carried on the event before the ambient scope is applied, so hold them aside and
	// merge them back on top: applyScope would otherwise overwrite them with the process-wide map.
	perEvent := e.Tags
	e.Tags = nil
	applyScope(&e)
	if len(perEvent) > 0 {
		merged := make(map[string]string, len(e.Tags)+len(perEvent))
		for key, value := range e.Tags {
			merged[key] = value
		}
		for key, value := range perEvent {
			merged[key] = value
		}
		e.Tags = merged
	}

	body, err := json.Marshal(e)
	if err != nil {
		return SendResult{OK: false, Error: err.Error()}
	}
	return c.send(body)
}

func newEventID() string {
	var b [16]byte
	_, _ = rand.Read(b[:]) // 16 random bytes → 32 lowercase hex chars, the Sentry event_id shape
	return hex.EncodeToString(b[:])
}

func parseDSN(dsn string) (endpoint, projectID, publicKey string, err error) {
	u, parseErr := url.Parse(dsn)
	if parseErr != nil {
		return "", "", "", parseErr
	}
	projectID = strings.TrimPrefix(u.Path, "/")
	if u.User == nil || u.User.Username() == "" || u.Host == "" || projectID == "" {
		return "", "", "", errors.New("condux: DSN must be scheme://<key>@<host>/<projectID>")
	}
	return u.Scheme + "://" + u.Host, projectID, u.User.Username(), nil
}
