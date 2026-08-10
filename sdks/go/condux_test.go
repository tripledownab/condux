package condux

import (
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"regexp"
	"strings"
	"testing"
	"time"
)

const testDSN = "https://testkey@ingest.test/proj-uuid"

// stubRoundTripper replays a scripted sequence of responses (the last repeats) and records every request +
// body, so tests assert the wire shape and the retry behavior with no real network.
type stubRoundTripper struct {
	responses []func() (*http.Response, error)
	requests  []*http.Request
	bodies    []string
}

func (s *stubRoundTripper) RoundTrip(req *http.Request) (*http.Response, error) {
	body, _ := io.ReadAll(req.Body)
	s.requests = append(s.requests, req)
	s.bodies = append(s.bodies, string(body))
	idx := len(s.requests) - 1
	if idx >= len(s.responses) {
		idx = len(s.responses) - 1
	}
	return s.responses[idx]()
}

func respond(status int, retryAfter string) func() (*http.Response, error) {
	return func() (*http.Response, error) {
		h := http.Header{}
		if retryAfter != "" {
			h.Set("Retry-After", retryAfter)
		}
		return &http.Response{StatusCode: status, Header: h, Body: io.NopCloser(strings.NewReader(""))}, nil
	}
}

func networkError() func() (*http.Response, error) {
	return func() (*http.Response, error) { return nil, errors.New("connection refused") }
}

func newTestClient(t *testing.T, stub *stubRoundTripper, sleep func(time.Duration)) *Client {
	t.Helper()
	if sleep == nil {
		sleep = func(time.Duration) {}
	}
	c, err := New(Options{DSN: testDSN, Environment: "test", Release: "1.2.3", Transport: stub, Sleep: sleep})
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	return c
}

func TestCaptureException_EmitsSentryStoreShape(t *testing.T) {
	stub := &stubRoundTripper{responses: []func() (*http.Response, error){respond(200, "")}}
	res := newTestClient(t, stub, nil).CaptureException(errors.New("boom from go"))

	if !res.OK || res.Attempts != 1 {
		t.Fatalf("send: %+v", res)
	}

	var ev map[string]any
	if err := json.Unmarshal([]byte(stub.bodies[0]), &ev); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if id, _ := ev["event_id"].(string); !regexp.MustCompile(`^[0-9a-f]{32}$`).MatchString(id) {
		t.Errorf("event_id = %q, want 32 hex chars", ev["event_id"])
	}
	if _, ok := ev["timestamp"].(float64); !ok {
		t.Errorf("timestamp = %v, want a number", ev["timestamp"])
	}
	if ev["platform"] != "go" || ev["level"] != "error" || ev["environment"] != "test" || ev["release"] != "1.2.3" {
		t.Errorf("event meta = %v", ev)
	}

	ex := ev["exception"].(map[string]any)["values"].([]any)[0].(map[string]any)
	if ex["value"] != "boom from go" {
		t.Errorf("value = %v", ex["value"])
	}
	mech := ex["mechanism"].(map[string]any)
	if mech["type"] != "generic" || mech["handled"] != true {
		t.Errorf("mechanism = %v", mech)
	}
	frames := ex["stacktrace"].(map[string]any)["frames"].([]any)
	if len(frames) == 0 {
		t.Fatal("expected stack frames")
	}
	// Oldest-first: the newest (crashing) frame is last, and it is this in-app test function.
	top := frames[len(frames)-1].(map[string]any)
	if !strings.Contains(top["function"].(string), "TestCaptureException") || top["in_app"] != true {
		t.Errorf("top frame = %v", top)
	}

	if got := stub.requests[0].Header.Get("x-condux-auth"); got != "testkey" {
		t.Errorf("auth header = %q", got)
	}
	if got := stub.requests[0].URL.String(); got != "https://ingest.test/api/proj-uuid/store/" {
		t.Errorf("url = %q", got)
	}
}

func TestCaptureMessage_EmitsMessageWithoutException(t *testing.T) {
	stub := &stubRoundTripper{responses: []func() (*http.Response, error){respond(200, "")}}
	newTestClient(t, stub, nil).CaptureMessage("disk almost full", LevelWarning)

	var ev map[string]any
	_ = json.Unmarshal([]byte(stub.bodies[0]), &ev)
	if ev["level"] != "warning" || ev["message"] != "disk almost full" {
		t.Errorf("event = %v", ev)
	}
	if _, ok := ev["exception"]; ok {
		t.Errorf("message event should carry no exception: %v", ev)
	}
}

func TestRetries429HonoringRetryAfter(t *testing.T) {
	stub := &stubRoundTripper{responses: []func() (*http.Response, error){respond(429, "3"), respond(200, "")}}
	var delays []time.Duration
	res := newTestClient(t, stub, func(d time.Duration) { delays = append(delays, d) }).CaptureMessage("hi", LevelInfo)

	if !res.OK || res.Attempts != 2 {
		t.Fatalf("send: %+v", res)
	}
	if len(delays) != 1 || delays[0] != 3*time.Second {
		t.Errorf("delays = %v, want [3s]", delays)
	}
}

func TestRetries5xxWithExponentialBackoff(t *testing.T) {
	stub := &stubRoundTripper{responses: []func() (*http.Response, error){respond(503, ""), respond(503, ""), respond(200, "")}}
	var delays []time.Duration
	res := newTestClient(t, stub, func(d time.Duration) { delays = append(delays, d) }).CaptureMessage("hi", LevelInfo)

	if !res.OK || res.Attempts != 3 {
		t.Fatalf("send: %+v", res)
	}
	if len(delays) != 2 || delays[0] != 200*time.Millisecond || delays[1] != 400*time.Millisecond {
		t.Errorf("delays = %v, want [200ms 400ms]", delays)
	}
}

func TestDoesNotRetryClientError(t *testing.T) {
	stub := &stubRoundTripper{responses: []func() (*http.Response, error){respond(400, "")}}
	res := newTestClient(t, stub, nil).CaptureMessage("hi", LevelInfo)

	if res.OK || res.Attempts != 1 || res.Status != 400 {
		t.Errorf("send: %+v", res)
	}
}

func TestReportsNetworkErrorWithoutPanicking(t *testing.T) {
	stub := &stubRoundTripper{responses: []func() (*http.Response, error){networkError()}}
	c, _ := New(Options{DSN: testDSN, MaxRetries: 1, Transport: stub, Sleep: func(time.Duration) {}})
	res := c.CaptureMessage("hi", LevelInfo)

	if res.OK || res.Attempts != 2 || res.Status != 0 || res.Error == "" {
		t.Errorf("send: %+v", res)
	}
}

func TestNewRejectsMalformedDSN(t *testing.T) {
	if _, err := New(Options{DSN: "https://ingest.test/no-key"}); err == nil {
		t.Error("expected an error for a DSN with no key")
	}
}
