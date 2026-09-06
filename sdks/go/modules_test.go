package condux

import (
	"encoding/json"
	"net/http"
	"runtime/debug"
	"testing"
	"time"
)

// Runtime dependency inventory tests (ADR-0041). The relay reads a top-level "modules" string map, so
// the wire key is asserted directly: a renamed or nested key would be dropped silently by the parser
// rather than rejected.

func TestDeclaredModulesRideTheWireUnderTheSentryKey(t *testing.T) {
	clearModules()
	// Attachment enabled: setModules below replaces whatever the constructor collected. Disabling it
	// would skip attachment altogether and the declared map could never ride.
	client, stub := moduleTestClient(t, Options{})
	setModules(map[string]string{"github.com/acme/lib": "v1.6.0", "golang.org/x/net": "v0.24.0"})

	client.CaptureMessage("hi", LevelInfo)

	got := decodeEvent(t, stub, 0).Modules
	want := map[string]string{"github.com/acme/lib": "v1.6.0", "golang.org/x/net": "v0.24.0"}
	if len(got) != len(want) {
		t.Fatalf("modules = %v, want %v", got, want)
	}
	for name, version := range want {
		if got[name] != version {
			t.Errorf("modules[%q] = %q, want %q", name, got[name], version)
		}
	}
}

// An event from a client that declared nothing must keep its exact previous shape.
func TestAnEventCarriesNoModulesKeyWhenNoneWereDeclared(t *testing.T) {
	clearModules()
	client, stub := moduleTestClient(t, Options{DisableModules: true})

	client.CaptureMessage("hi", LevelInfo)

	var raw map[string]any
	if err := json.Unmarshal([]byte(stub.bodies[0]), &raw); err != nil {
		t.Fatal(err)
	}
	if _, present := raw["modules"]; present {
		t.Error("an event with no inventory should carry no modules key at all")
	}
}

// What makes the feature affordable: the server deduplicates a release's inventory to one row per
// package per day, so repeating the map on every event spends bytes for nothing.
func TestTheInventoryRidesTheFirstEventThenWaitsOutTheInterval(t *testing.T) {
	clearModules()
	client, stub := moduleTestClient(t, Options{})
	setModules(map[string]string{"github.com/acme/lib": "v1.6.0"})

	client.CaptureMessage("one", LevelInfo)
	client.CaptureMessage("two", LevelInfo)
	client.CaptureMessage("three", LevelInfo)

	carrying := 0
	for i := range stub.bodies {
		if len(decodeEvent(t, stub, i).Modules) > 0 {
			carrying++
		}
	}
	if carrying != 1 {
		t.Errorf("%d events carried the inventory, want exactly 1", carrying)
	}
}

// Repeating matters as much as skipping: the event carrying the inventory can be dropped by a rate
// limit before anything parses it, so one attempt per process would lose that day's inventory.
func TestTheInventoryRidesAgainOnceTheIntervalElapses(t *testing.T) {
	clearModules()
	setModules(map[string]string{"github.com/acme/lib": "v1.6.0"})
	start := time.Unix(1_700_000_000, 0)

	if modulesField(start) == nil {
		t.Fatal("the first call should carry the inventory")
	}
	if modulesField(start.Add(modulesInterval-time.Second)) != nil {
		t.Error("inside the interval the inventory should be withheld")
	}
	if modulesField(start.Add(modulesInterval)) == nil {
		t.Error("once the interval elapses the inventory should ride again")
	}
}

func TestAFreshDeclarationDoesNotWaitOutThePreviousInterval(t *testing.T) {
	clearModules()
	setModules(map[string]string{"github.com/acme/lib": "v1.6.0"})
	start := time.Unix(1_700_000_000, 0)
	modulesField(start)

	setModules(map[string]string{"github.com/acme/lib": "v2.0.0"})

	got := modulesField(start.Add(time.Second))
	if got["github.com/acme/lib"] != "v2.0.0" {
		t.Errorf("a fresh declaration should ride immediately, got %v", got)
	}
}

func TestEntriesAreCappedSoOneEventCannotCarryAnUnboundedMap(t *testing.T) {
	clearModules()
	many := make(map[string]string, maxModules+50)
	for i := 0; i < maxModules+50; i++ {
		many[string(rune('a'+i%26))+string(rune('a'+i/26))+itoa(i)] = "v1.0.0"
	}
	setModules(many)

	if got := len(modulesField(time.Unix(1, 0))); got != maxModules {
		t.Errorf("carried %d entries, want the cap of %d", got, maxModules)
	}
}

func TestBlankEntriesAreDroppedRatherThanReportedAsVersions(t *testing.T) {
	clearModules()
	setModules(map[string]string{"good": "v1.0.0", "blank": "", "": "v2.0.0"})

	got := modulesField(time.Unix(1, 0))
	if len(got) != 1 || got["good"] != "v1.0.0" {
		t.Errorf("modules = %v, want only the usable entry", got)
	}
}

// The build info this reads is what the toolchain records into the binary, so under `go test` the test
// binary itself has one. This asserts the shape rather than a specific dependency, since the module
// graph of a test binary is not this SDK's to pin.
func TestCollectReadsTheBuildInfoRecordedInTheBinary(t *testing.T) {
	info, ok := debug.ReadBuildInfo()
	if !ok {
		t.Skip("this binary carries no build info, so there is nothing to read")
	}

	got := collectModules()
	for name, version := range got {
		if name == "" || version == "" {
			t.Errorf("collected a blank entry: %q = %q", name, version)
		}
		if version == "(devel)" {
			t.Errorf("%q reported the placeholder version, which matches no advisory", name)
		}
	}
	// Every dependency the toolchain recorded with a real version should be present, since dropping one
	// would silently narrow the inventory.
	for _, dep := range info.Deps {
		if dep == nil || dep.Replace != nil || !isRealVersion(dep.Version) {
			continue
		}
		if got[dep.Path] != dep.Version {
			t.Errorf("dependency %q: collected %q, build info says %q", dep.Path, got[dep.Path], dep.Version)
		}
	}
}

// A replace directive means the built code is NOT the version the requirement names. Reporting the
// replaced-away version would name a version this binary does not contain, which is the one kind of
// wrong answer this feature must not give.
func TestPlaceholderVersionsAreNotReported(t *testing.T) {
	if isRealVersion("(devel)") {
		t.Error("(devel) is a placeholder, not a version")
	}
	if isRealVersion("") {
		t.Error("an empty version is not a version")
	}
	if !isRealVersion("v1.6.0") {
		t.Error("a real version should be reported")
	}
}

// Reuses the suite's stubRoundTripper rather than adding a second recording transport, so there is one
// place that knows how a request body is captured.
func moduleTestClient(t *testing.T, opts Options) (*Client, *stubRoundTripper) {
	t.Helper()
	stub := &stubRoundTripper{responses: []func() (*http.Response, error){respond(200, "")}}
	opts.DSN = testDSN
	opts.Transport = stub
	opts.Sleep = func(time.Duration) {}
	client, err := New(opts)
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	return client, stub
}

func decodeEvent(t *testing.T, stub *stubRoundTripper, index int) event {
	t.Helper()
	var ev event
	if err := json.Unmarshal([]byte(stub.bodies[index]), &ev); err != nil {
		t.Fatalf("decode event %d: %v", index, err)
	}
	return ev
}

func itoa(n int) string {
	if n == 0 {
		return "0"
	}
	digits := ""
	for n > 0 {
		digits = string(rune('0'+n%10)) + digits
		n /= 10
	}
	return digits
}

// The opt-out is enforced twice on purpose, and this covers the half the other tests cannot reach.
//
// New declines to collect, which is what saves the work. dispatch also declines to attach, which is
// what keeps a disabled client honest: the inventory lives in package state shared by every client in
// the process, so a second client that enabled it would otherwise put modules on this one's events.
// Declaring an inventory AFTER constructing a disabled client is the only way to exercise that guard,
// because the constructor's own clearing would otherwise mask it.
func TestADisabledClientAttachesNothingEvenWhenAnInventoryIsDeclared(t *testing.T) {
	clearModules()
	client, stub := moduleTestClient(t, Options{DisableModules: true})
	setModules(map[string]string{"github.com/acme/lib": "v1.6.0"})

	client.CaptureMessage("hi", LevelInfo)

	if got := decodeEvent(t, stub, 0).Modules; len(got) != 0 {
		t.Errorf("a disabled client attached %v, want nothing", got)
	}
}

// The clock is a caller-supplied hook, so it must be read once per event. Reading it again for the
// inventory would advance a clock that returns a sequence, and would date the event and the interval
// decision from two different instants.
func TestTheClockIsReadOncePerEvent(t *testing.T) {
	clearModules()
	calls := 0
	stub := &stubRoundTripper{responses: []func() (*http.Response, error){respond(200, "")}}
	client, err := New(Options{
		DSN:       testDSN,
		Transport: stub,
		Sleep:     func(time.Duration) {},
		Now:       func() time.Time { calls++; return time.Unix(1_700_000_000, 0) },
	})
	if err != nil {
		t.Fatalf("New: %v", err)
	}

	client.CaptureMessage("hi", LevelInfo)

	if calls != 1 {
		t.Errorf("the clock was read %d times for one event, want 1", calls)
	}
}
