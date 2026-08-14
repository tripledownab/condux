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
	e.Timestamp = float64(c.now().UnixNano()) / float64(time.Second) // epoch seconds, the store convention
	e.Platform = "go"
	e.Environment = c.opts.Environment
	e.Release = c.opts.Release
	applyScope(&e)

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
