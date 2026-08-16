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
				InApp:    isInApp(f.File, goroot),
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

// isInApp reports whether a frame is the application's own code.
//
// Excludes the standard library by GOROOT, and dependencies by the module cache path. The second half
// brings Go into line with the rest of the fleet, which all exclude wherever their ecosystem installs
// dependencies: site-packages, /gems/, /vendor/, node_modules. Without it EVERY dependency counted as
// application code, and so did this SDK, whose own frames therefore entered the customer's grouping
// fingerprint and could be chosen as the culprit.
//
// A vendored build (vendor/ beside the source) is deliberately NOT excluded here: those files are inside
// the user's own tree and a path check cannot tell them from application code without guessing.
func isInApp(file string, goroot string) bool {
	if file == "" {
		return false
	}
	if goroot != "" && strings.HasPrefix(file, goroot) {
		return false
	}
	// The module cache, wherever GOMODCACHE puts it. Matched as a path segment so a user directory
	// merely called "mod" cannot collide.
	return !strings.Contains(file, "/pkg/mod/")
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
