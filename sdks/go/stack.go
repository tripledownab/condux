package condux

import (
	"runtime"
	"strings"
)

// captureStack records the calling goroutine's stack at capture time (Go errors do not carry their own
// stack), as Sentry frames ordered oldest-first (the crashing frame last) — the order the relay's
// fingerprinter and issue detail expect. This SDK's own capture path is dropped, and standard-library
// frames are marked out-of-app.
func captureStack() []frame {
	pcs := make([]uintptr, 64)
	n := runtime.Callers(2, pcs) // skip runtime.Callers + captureStack itself
	if n == 0 {
		return nil
	}

	goroot := runtime.GOROOT()
	frames := runtime.CallersFrames(pcs[:n])
	var out []frame
	for {
		f, more := frames.Next()
		if !isCaptureFrame(f.Function) { // drop the SDK's own capture machinery (not user code in-module)
			out = append(out, frame{
				Filename: f.File,
				Function: f.Function,
				Module:   packageOf(f.Function),
				Lineno:   f.Line,
				InApp:    f.File != "" && (goroot == "" || !strings.HasPrefix(f.File, goroot)),
			})
		}
		if !more {
			break
		}
	}

	for i, j := 0, len(out)-1; i < j; i, j = i+1, j-1 {
		out[i], out[j] = out[j], out[i]
	}
	return out
}

// isCaptureFrame reports whether a runtime function name is part of this SDK's capture path (so it is
// dropped from the reported stack). Matched by exact suffix, not module, so user code sharing the module
// (this SDK's own tests) is kept.
func isCaptureFrame(fn string) bool {
	for _, suffix := range []string{
		".captureStack", ".toException",
		".(*Client).CaptureException", ".(*Client).CaptureMessage",
	} {
		if strings.HasSuffix(fn, suffix) {
			return true
		}
	}
	return false
}

// packageOf extracts the package path from a runtime function name like
// "github.com/acme/app/pkg.(*T).Method" → "github.com/acme/app/pkg".
func packageOf(fn string) string {
	slash := strings.LastIndex(fn, "/")
	dot := strings.Index(fn[slash+1:], ".")
	if dot < 0 {
		return fn
	}
	return fn[:slash+1+dot]
}
