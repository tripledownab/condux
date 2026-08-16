package condux

import "testing"

// The fleet-wide rule: the standard library, this SDK and every dependency are not application code.
//
// Go was the last SDK still calling dependencies in-app, because it only excluded GOROOT. That mattered
// beyond tidiness: in_app drives the relay's grouping fingerprint, so a dependency upgrade that shifted
// those frames split one issue into two, and this SDK's own frames were candidates for the culprit.
func TestIsInApp(t *testing.T) {
	const goroot = "/usr/local/go"
	cases := []struct {
		name string
		file string
		want bool
	}{
		{"application code", "/home/dev/app/internal/checkout/order.go", true},
		{"standard library", goroot + "/src/net/http/server.go", false},
		{"a dependency in the module cache", "/home/dev/go/pkg/mod/github.com/gin-gonic/gin@v1.10.0/context.go", false},
		{"this SDK, itself a module", "/home/dev/go/pkg/mod/github.com/tripledownab/condux/sdks/go@v0.1.6/client.go", false},
		{"no file", "", false},
		{"a user directory merely called mod", "/home/dev/app/mod/handler.go", true},
	}
	for _, c := range cases {
		if got := isInApp(c.file, goroot); got != c.want {
			t.Errorf("%s: isInApp(%q) = %v, want %v", c.name, c.file, got, c.want)
		}
	}
}
