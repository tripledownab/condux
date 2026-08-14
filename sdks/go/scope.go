package condux

import (
	"sync"
	"time"
)

// Ambient event enrichment: who the user is, which tags and contexts apply, and the breadcrumb trail
// leading up to an error. Set once (or as the app's state changes) and every subsequent event carries
// it — the first triage questions ("which customer, which plan, what did they do last") answered
// without threading anything through capture calls. The relay already scrubs all of these at ingest and
// derives the pseudonymous users-affected key from the user fields.
//
// The scope is process wide, like every other Condux SDK, so it applies whichever Client reports. Guarded
// by a mutex because Go programs really do capture from many goroutines.

// MaxBreadcrumbs is the trail length kept: newest wins, so a long-lived process drops the oldest crumbs
// rather than growing without bound.
const MaxBreadcrumbs = 30

// User identifies the signed-in user. The relay hashes the strongest identifier and redacts the raw email.
type User struct {
	ID       string `json:"id,omitempty"`
	Email    string `json:"email,omitempty"`
	Username string `json:"username,omitempty"`
}

// Breadcrumb is one step of the trail: a navigation, a job, a request — whatever helps replay the path.
type Breadcrumb struct {
	Message  string         `json:"message"`
	Category string         `json:"category,omitempty"`
	Level    Level          `json:"level,omitempty"`
	Type     string         `json:"type,omitempty"`
	Data     map[string]any `json:"data,omitempty"`
	// Timestamp is epoch seconds; stamped for you when zero.
	Timestamp float64 `json:"timestamp"`
}

var scope struct {
	mu          sync.Mutex
	user        *User
	tags        map[string]string
	contexts    map[string]map[string]any
	breadcrumbs []Breadcrumb
}

// SetUser attaches the signed-in user to subsequent events; nil clears it (a sign-out).
func SetUser(user *User) {
	scope.mu.Lock()
	defer scope.mu.Unlock()
	if user == nil {
		scope.user = nil
		return
	}
	copied := *user
	scope.user = &copied
}

// SetTag attaches a tag to subsequent events. Use RemoveTag to drop one (Go has no nil string, so the
// removal is its own call rather than a magic empty value).
func SetTag(key, value string) {
	scope.mu.Lock()
	defer scope.mu.Unlock()
	if scope.tags == nil {
		scope.tags = map[string]string{}
	}
	scope.tags[key] = value
}

// RemoveTag drops a tag set earlier.
func RemoveTag(key string) {
	scope.mu.Lock()
	defer scope.mu.Unlock()
	delete(scope.tags, key)
}

// SetContext attaches a named context object to subsequent events; a nil context removes it.
func SetContext(name string, context map[string]any) {
	scope.mu.Lock()
	defer scope.mu.Unlock()
	if context == nil {
		delete(scope.contexts, name)
		return
	}
	if scope.contexts == nil {
		scope.contexts = map[string]map[string]any{}
	}
	copied := make(map[string]any, len(context))
	for key, value := range context {
		copied[key] = value
	}
	scope.contexts[name] = copied
}

// AddBreadcrumb records a breadcrumb; the trail (newest last, capped at MaxBreadcrumbs) rides every
// subsequent event.
func AddBreadcrumb(crumb Breadcrumb) {
	if crumb.Timestamp == 0 {
		crumb.Timestamp = float64(time.Now().UnixNano()) / float64(time.Second)
	}
	scope.mu.Lock()
	defer scope.mu.Unlock()
	scope.breadcrumbs = append(scope.breadcrumbs, crumb)
	if len(scope.breadcrumbs) > MaxBreadcrumbs {
		scope.breadcrumbs = append([]Breadcrumb(nil), scope.breadcrumbs[len(scope.breadcrumbs)-MaxBreadcrumbs:]...)
	}
}

// ClearScope resets all ambient state (tests, or a full sign-out).
func ClearScope() {
	scope.mu.Lock()
	defer scope.mu.Unlock()
	scope.user = nil
	scope.tags = nil
	scope.contexts = nil
	scope.breadcrumbs = nil
}

// applyScope copies the scope onto an event, leaving unset parts absent so an unenriched event keeps its
// exact wire shape.
func applyScope(e *event) {
	scope.mu.Lock()
	defer scope.mu.Unlock()

	if scope.user != nil {
		copied := *scope.user
		e.User = &copied
	}
	if len(scope.tags) > 0 {
		e.Tags = make(map[string]string, len(scope.tags))
		for key, value := range scope.tags {
			e.Tags[key] = value
		}
	}
	if len(scope.contexts) > 0 {
		e.Contexts = make(map[string]map[string]any, len(scope.contexts))
		for name, values := range scope.contexts {
			e.Contexts[name] = values
		}
	}
	if len(scope.breadcrumbs) > 0 {
		e.Breadcrumbs = &breadcrumbs{Values: append([]Breadcrumb(nil), scope.breadcrumbs...)}
	}
}
