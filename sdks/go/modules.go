package condux

import (
	"runtime/debug"
	"sort"
	"sync"
	"time"
)

// The runtime dependency inventory that rides events (ADR-0041): which module versions are actually
// built into this binary, as opposed to which ones a go.mod declares. The relay indexes this so a
// security advisory can be answered with "and you are running v1.6.0 in production" rather than only
// "your go.mod says so".
//
// Go has the best source of this in the fleet, and it needs no filesystem walk and no guessing: the
// toolchain records the exact resolved version of every dependency INTO the binary at build time, and
// debug.ReadBuildInfo reads it back. Verified against a real third-party binary, which recorded 221
// dependencies with exact versions.

// maxModules is the most entries carried on one event. The cap applies after sorting, so which entries
// survive is stable across events rather than varying with map order: the server sees one consistent
// set instead of a shifting sample.
const maxModules = 1000

// modulesInterval is how long to wait before repeating the inventory on another event.
//
// This is what makes the feature affordable. The server deduplicates a release's inventory down to one
// row per package per day, so attaching the whole map to every event would spend bytes for nothing.
// Repeating on an interval rather than sending once keeps the robustness that every-event buys: the
// event carrying the inventory can be dropped by a rate limit or a quota rejection before anything
// parses it, so a single attempt per process would lose that day's inventory outright.
const modulesInterval = 15 * time.Minute

var (
	modulesMu       sync.Mutex
	modules         map[string]string
	lastAttachedAt  time.Time
	hasAttachedOnce bool
)

// collectModules reads the module versions recorded in this binary, empty when there are none.
//
// A binary built without module information, which is what `go build` on a GOPATH-style project or a
// heavily stripped build produces, reports nothing. Unknown is the honest answer there, and it is the
// same answer the server already understands from every SDK that cannot enumerate.
func collectModules() map[string]string {
	info, ok := debug.ReadBuildInfo()
	if !ok {
		return nil
	}

	found := make(map[string]string, len(info.Deps)+1)
	// The main module is the application itself. It is included when it carries a real version, and
	// skipped when it does not: an unreleased build reports "(devel)", which is not a version and would
	// render as "running (devel)" against an advisory it can never match.
	if isRealVersion(info.Main.Version) {
		found[info.Main.Path] = info.Main.Version
	}
	for _, dep := range info.Deps {
		if dep == nil {
			continue
		}
		// A replace directive means the built code is NOT the version the requirement names, so report
		// what was actually built. Reporting the replaced-away version would name a version this binary
		// does not contain, which is the one kind of wrong answer this feature must not give.
		effective := dep
		if dep.Replace != nil {
			effective = dep.Replace
		}
		if effective.Path != "" && isRealVersion(effective.Version) {
			// The requirement's module path is the name the ecosystem knows, even under a replace: OSV
			// indexes advisories against the required module, not against a local fork's path.
			found[dep.Path] = effective.Version
		}
	}
	return found
}

// isRealVersion rejects the placeholders the toolchain uses when there is no released version to name.
func isRealVersion(version string) bool {
	return version != "" && version != "(devel)"
}

// initModules declares the inventory a new client will report, or clears it when the client asked for
// none. Called once per client rather than per capture: a binary's build info is fixed, so there is
// nothing to re-read. Set explicitly either way, so constructing a client with the inventory disabled
// cannot inherit one a previous client declared, since this state is package-wide.
func initModules(disabled bool) {
	if disabled {
		setModules(nil)
		return
	}
	setModules(collectModules())
}

// setModules declares the module versions for subsequent events; nil clears them.
func setModules(next map[string]string) {
	modulesMu.Lock()
	defer modulesMu.Unlock()

	// A fresh declaration is news, so let the next event carry it rather than waiting out an interval
	// started by the previous inventory.
	hasAttachedOnce = false

	if len(next) == 0 {
		modules = nil
		return
	}

	names := make([]string, 0, len(next))
	for name, version := range next {
		if name != "" && version != "" {
			names = append(names, name)
		}
	}
	sort.Strings(names)
	if len(names) > maxModules {
		names = names[:maxModules]
	}

	trimmed := make(map[string]string, len(names))
	for _, name := range names {
		trimmed[name] = next[name]
	}
	if len(trimmed) == 0 {
		modules = nil
		return
	}
	modules = trimmed
}

// modulesField returns the inventory to attach to an event: the full map on the first event and then at
// most once per modulesInterval, and nil otherwise, so an event that carries nothing keeps its exact
// previous wire shape.
func modulesField(now time.Time) map[string]string {
	modulesMu.Lock()
	defer modulesMu.Unlock()

	if modules == nil {
		return nil
	}
	if hasAttachedOnce && now.Sub(lastAttachedAt) < modulesInterval {
		return nil
	}

	lastAttachedAt = now
	hasAttachedOnce = true
	return modules
}

// clearModules resets the inventory and its interval. Tests only; a binary has one build.
func clearModules() {
	modulesMu.Lock()
	defer modulesMu.Unlock()
	modules = nil
	lastAttachedAt = time.Time{}
	hasAttachedOnce = false
}
