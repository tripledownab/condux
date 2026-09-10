package condux

import (
	"bufio"
	"fmt"
	"io"
	"net/http"
	"os"
	"strconv"
	"strings"
	"testing"
	"time"
)

// Drives sdks/conformance/backoff.tsv, the retry schedule every Condux SDK owes. The cases live in that
// file rather than here so the seven transports assert against one artifact instead of seven readings of
// one sentence in a comment. See the file for why it is data and not prose.

// A symlink to ../conformance/backoff.tsv, and it has to be, rather than the relative path itself.
// go test caches a package's result and rechecks the files the test opened, but only those inside the
// module root: cmd/go/internal/test/test.go says "Do not recheck files outside the module, GOPATH, or
// GOROOT root" and skips them. The fixture is shared by seven SDKs, so it lives above this module, and
// reaching it directly meant editing it changed nothing that go test could see. Measured: a warm cache
// reported "ok (cached)" over a fixture whose expected value had been changed to a wrong one. The
// symlink puts a path inside the module in front of the same bytes, so the content hash moves with it.
const backoffFixture = "testdata/backoff.tsv"

type backoffCase struct {
	attempt    int
	status     int
	retryAfter string // "" means the header is absent, which in Go is also how a blank one reads
	present    bool
	expectedMs int
}

func readBackoffCases(t *testing.T) []backoffCase {
	t.Helper()
	file, err := os.Open(backoffFixture)
	if err != nil {
		t.Fatalf("open %s: %v", backoffFixture, err)
	}
	defer file.Close()

	var cases []backoffCase
	scanner := bufio.NewScanner(file)
	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		columns := strings.Split(line, "\t")
		if len(columns) != 4 {
			t.Fatalf("malformed fixture row %q: want 4 tab-separated columns", line)
		}
		c := backoffCase{
			attempt:    mustAtoi(t, columns[0]),
			status:     mustAtoi(t, columns[1]),
			expectedMs: mustAtoi(t, columns[3]),
		}
		// http.Header.Get returns "" for a header that is absent and for one that is present and blank,
		// so those two fixture cases are one case here. Both expect the exponential fallback, so nothing
		// is lost; a Go SDK simply has no way to tell them apart and no reason to.
		switch columns[2] {
		case "<none>", "<empty>":
			c.present = columns[2] == "<empty>"
		default:
			c.retryAfter, c.present = columns[2], true
		}
		cases = append(cases, c)
	}
	if err := scanner.Err(); err != nil {
		t.Fatalf("read %s: %v", backoffFixture, err)
	}
	// A fixture that failed to load reads exactly like one where every case passed, so the count is
	// asserted rather than assumed. A floor, so adding a case does not mean editing seven SDKs.
	if len(cases) < 15 {
		t.Fatalf("%s looks truncated: %d case(s)", backoffFixture, len(cases))
	}
	return cases
}

func mustAtoi(t *testing.T, s string) int {
	t.Helper()
	n, err := strconv.Atoi(s)
	if err != nil {
		t.Fatalf("fixture column %q is not an integer: %v", s, err)
	}
	return n
}

func TestBackoffMatchesTheFleetContract(t *testing.T) {
	for _, c := range readBackoffCases(t) {
		name := fmt.Sprintf("attempt=%d/status=%d/retry-after=%q", c.attempt, c.status, c.retryAfter)
		t.Run(name, func(t *testing.T) {
			var delays []time.Duration
			stub := &stubRoundTripper{responses: []func() (*http.Response, error){
				func() (*http.Response, error) {
					h := http.Header{}
					if c.present {
						h.Set("Retry-After", c.retryAfter)
					}
					return &http.Response{
						StatusCode: c.status,
						Header:     h,
						Body:       io.NopCloser(strings.NewReader("")),
					}, nil
				},
			}}
			// One more retry than the attempt under test, so the sleep that follows it is recorded. The
			// scripted response repeats, so every attempt fails and the schedule runs to its end.
			client, err := New(Options{
				DSN:        testDSN,
				MaxRetries: c.attempt + 1,
				Transport:  stub,
				Sleep:      func(d time.Duration) { delays = append(delays, d) },
			})
			if err != nil {
				t.Fatalf("New: %v", err)
			}

			client.CaptureMessage("hi", LevelError)

			if len(delays) != c.attempt+1 {
				t.Fatalf("recorded %d sleep(s), want %d", len(delays), c.attempt+1)
			}
			if got := int(delays[c.attempt] / time.Millisecond); got != c.expectedMs {
				t.Errorf("waited %dms, the fleet contract says %dms", got, c.expectedMs)
			}
		})
	}
}
