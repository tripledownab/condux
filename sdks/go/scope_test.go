package condux

import (
	"encoding/json"
	"errors"
	"net/http"
	"testing"
)

// captureWith runs one capture through a stub transport and returns the JSON that reached the relay.
func captureWith(t *testing.T, capture func(*Client)) map[string]any {
	t.Helper()
	stub := &stubRoundTripper{responses: []func() (*http.Response, error){respond(200, "")}}
	capture(newTestClient(t, stub, nil))

	var ev map[string]any
	if err := json.Unmarshal([]byte(stub.bodies[0]), &ev); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	return ev
}

// The scope is process wide, so every test here restores it; leaking would enrich (and so break) the
// wire assertions in the rest of the suite.
func TestUnenrichedEventCarriesNoneOfTheScopeKeys(t *testing.T) {
	ClearScope()
	ev := captureWith(t, func(c *Client) { c.CaptureMessage("plain", LevelInfo) })

	for _, key := range []string{"user", "tags", "contexts", "breadcrumbs"} {
		if _, present := ev[key]; present {
			t.Errorf("unenriched event should not carry %q: %v", key, ev)
		}
	}
}

func TestEnrichedEventCarriesTheSentryWireShape(t *testing.T) {
	ClearScope()
	defer ClearScope()
	SetUser(&User{ID: "42", Email: "dev@example.test"})
	SetTag("plan", "team")
	SetContext("device", map[string]any{"model": "laptop"})
	AddBreadcrumb(Breadcrumb{Message: "/checkout", Category: "navigation", Level: LevelInfo})

	ev := captureWith(t, func(c *Client) { c.CaptureMessage("after enrichment", LevelInfo) })

	user := ev["user"].(map[string]any)
	if user["id"] != "42" || user["email"] != "dev@example.test" {
		t.Errorf("user = %v", user)
	}
	if tags := ev["tags"].(map[string]any); tags["plan"] != "team" {
		t.Errorf("tags = %v", tags)
	}
	contexts := ev["contexts"].(map[string]any)["device"].(map[string]any)
	if contexts["model"] != "laptop" {
		t.Errorf("contexts = %v", ev["contexts"])
	}
	// Breadcrumbs ride the Sentry {"values": [...]} envelope, not a bare array.
	crumbs := ev["breadcrumbs"].(map[string]any)["values"].([]any)
	if len(crumbs) != 1 {
		t.Fatalf("breadcrumbs = %v", crumbs)
	}
	crumb := crumbs[0].(map[string]any)
	if crumb["message"] != "/checkout" || crumb["category"] != "navigation" || crumb["level"] != "info" {
		t.Errorf("breadcrumb = %v", crumb)
	}
	if stamp, ok := crumb["timestamp"].(float64); !ok || stamp == 0 {
		t.Errorf("breadcrumb timestamp = %v, want epoch seconds stamped for us", crumb["timestamp"])
	}
}

func TestScopeRidesAnExceptionCaptureToo(t *testing.T) {
	ClearScope()
	defer ClearScope()
	SetTag("plan", "business")

	ev := captureWith(t, func(c *Client) { c.CaptureException(errors.New("boom")) })

	if tags := ev["tags"].(map[string]any); tags["plan"] != "business" {
		t.Errorf("tags = %v", tags)
	}
}

func TestRemovingScopeEntriesDropsTheKeys(t *testing.T) {
	ClearScope()
	defer ClearScope()
	SetUser(&User{ID: "42"})
	SetTag("plan", "team")
	SetContext("device", map[string]any{"model": "laptop"})

	SetUser(nil)
	RemoveTag("plan")
	SetContext("device", nil)
	ev := captureWith(t, func(c *Client) { c.CaptureMessage("after clearing", LevelInfo) })

	for _, key := range []string{"user", "tags", "contexts"} {
		if _, present := ev[key]; present {
			t.Errorf("cleared event should not carry %q: %v", key, ev)
		}
	}
}

func TestBreadcrumbTrailIsCappedDroppingTheOldest(t *testing.T) {
	ClearScope()
	defer ClearScope()
	for index := 0; index < 35; index++ {
		AddBreadcrumb(Breadcrumb{Message: string(rune('a' + index%26)), Data: map[string]any{"i": index}})
	}

	ev := captureWith(t, func(c *Client) { c.CaptureMessage("after many steps", LevelInfo) })

	crumbs := ev["breadcrumbs"].(map[string]any)["values"].([]any)
	if len(crumbs) != MaxBreadcrumbs {
		t.Fatalf("kept %d breadcrumbs, want %d", len(crumbs), MaxBreadcrumbs)
	}
	// Newest last, oldest dropped: the trail starts at step 5 and ends at step 34.
	first := crumbs[0].(map[string]any)["data"].(map[string]any)
	last := crumbs[len(crumbs)-1].(map[string]any)["data"].(map[string]any)
	if first["i"] != float64(5) || last["i"] != float64(34) {
		t.Errorf("trail spans %v..%v, want 5..34", first["i"], last["i"])
	}
}

func TestCaptureUnhandledMarksTheMechanism(t *testing.T) {
	ClearScope()
	handled := captureWith(t, func(c *Client) { c.CaptureException(errors.New("boom")) })
	unhandled := captureWith(t, func(c *Client) { c.CaptureUnhandled(errors.New("boom")) })

	if mechanismHandled(t, handled) != true {
		t.Error("CaptureException should report handled: true")
	}
	// Without this the relay can never mark a Go issue unhandled, whatever the app does.
	if mechanismHandled(t, unhandled) != false {
		t.Error("CaptureUnhandled should report handled: false")
	}
}

func mechanismHandled(t *testing.T, ev map[string]any) bool {
	t.Helper()
	ex := ev["exception"].(map[string]any)["values"].([]any)[0].(map[string]any)
	return ex["mechanism"].(map[string]any)["handled"].(bool)
}

// A nil Client is what a program holds when New returned an error and the result was kept anyway. Capture
// must drop the event rather than panic: panicking here would crash the app in the exact path where it is
// already handling a failure.
func TestNilClientCaptureDoesNotPanic(t *testing.T) {
	var client *Client

	message := client.CaptureMessage("dropped", LevelInfo)
	exception := client.CaptureException(errors.New("dropped"))

	for _, result := range []SendResult{message, exception} {
		if result.OK || result.Error != "not_initialized" {
			t.Errorf("nil-client capture = %+v, want a not_initialized failure", result)
		}
	}
}
