package testevent

import (
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

// The exit code is the contract a CI script branches on, so assert the code, not just the output.
// Delivery runs against a real local HTTP server: the command's whole purpose is proving the actual
// transport works, and a stub transport here would test nothing the SDK's own suite does not.
func TestNoDsnIsAUsageError(t *testing.T) {
	if code := Run("", "", io.Discard, io.Discard); code != 2 {
		t.Errorf("exit code = %d, want 2", code)
	}
}

func TestMalformedDsnIsAUsageError(t *testing.T) {
	if code := Run("https://ingest.test/no-key", "", io.Discard, io.Discard); code != 2 {
		t.Errorf("exit code = %d, want 2", code)
	}
}

func TestDeliveryToALiveRelayExitsZero(t *testing.T) {
	var path string
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		path = r.URL.Path
		w.WriteHeader(200)
	}))
	defer server.Close()

	out := &strings.Builder{}
	code := Run(strings.Replace(server.URL, "http://", "http://key@", 1)+"/1", "hello", out, io.Discard)

	if code != 0 {
		t.Errorf("exit code = %d, want 0", code)
	}
	if path != "/api/1/store/" {
		t.Errorf("posted to %q, want the store endpoint", path)
	}
	if !strings.Contains(out.String(), "Delivered") {
		t.Errorf("output = %q, want a delivery confirmation", out.String())
	}
}

func TestARefusedRelayExitsOne(t *testing.T) {
	// Port 9 (discard) refuses immediately, so the retry loop gives up without waiting on a timeout.
	if code := Run("http://key@127.0.0.1:9/1", "", io.Discard, io.Discard); code != 1 {
		t.Errorf("exit code = %d, want 1", code)
	}
}
