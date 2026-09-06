package condux

import (
	"net/http"
	"reflect"
)

// Level is the event severity, matching the levels the relay understands. The wire value is the lowercase
// string.
type Level string

const (
	LevelDebug   Level = "debug"
	LevelInfo    Level = "info"
	LevelWarning Level = "warning"
	LevelError   Level = "error"
	LevelFatal   Level = "fatal"
)

// SendResult is the outcome of a delivery attempt sequence. Reporting never panics — inspect OK. Status is
// the last HTTP status seen (0 if every attempt was a network error); Error is the last transport error.
type SendResult struct {
	OK       bool
	Attempts int
	Status   int
	Error    string
}

// The Sentry store wire shape. Serialized to snake_case JSON so the relay parses it like a Sentry SDK.
// Every enrichment field is omitempty, so an unenriched event carries none of those keys at all.
type event struct {
	EventID     string                    `json:"event_id"`
	Timestamp   float64                   `json:"timestamp"`
	Platform    string                    `json:"platform"`
	Level       Level                     `json:"level"`
	Environment string                    `json:"environment,omitempty"`
	Release     string                    `json:"release,omitempty"`
	Message     string                    `json:"message,omitempty"`
	Exception   *exceptions               `json:"exception,omitempty"`
	User        *User                     `json:"user,omitempty"`
	Tags        map[string]string         `json:"tags,omitempty"`
	Contexts    map[string]map[string]any `json:"contexts,omitempty"`
	Breadcrumbs *breadcrumbs              `json:"breadcrumbs,omitempty"`
	Request     *Request                  `json:"request,omitempty"`
	// Modules is the runtime dependency inventory (ADR-0041): the module versions built into this
	// binary. omitempty, so an event that carries none keeps its exact previous wire shape.
	Modules map[string]string `json:"modules,omitempty"`
}

// Request is the request an event happened during. The JSON names are the Sentry store shape the relay
// parses, so query_string is snake_case on purpose.
//
// Deliberately no headers: they carry Cookie and Authorization, and while the relay scrubs sensitive
// keys at ingest, not sending credentials at all is the stronger guarantee.
type Request struct {
	// URL is the path or absolute URL, without the query string.
	URL         string `json:"url,omitempty"`
	Method      string `json:"method,omitempty"`
	QueryString string `json:"query_string,omitempty"`
}

// RequestFrom describes an inbound *http.Request for reporting. Nil-safe, so a handler that captures
// outside a request does not have to branch.
func RequestFrom(r *http.Request) *Request {
	if r == nil {
		return nil
	}
	request := &Request{Method: r.Method}
	if r.URL != nil {
		request.URL = r.URL.Path
		request.QueryString = r.URL.RawQuery
	}
	return request
}

// CaptureContext carries detail belonging to one event rather than to the process.
//
// This exists because the package scope (SetUser, SetTag) is global: a Go server handles requests on
// many goroutines at once, so request detail set there attaches to whichever event is captured next,
// which may belong to a different request. A wrong URL is worse than none, because it sends whoever is
// debugging to the wrong endpoint.
type CaptureContext struct {
	Request *Request
	// Tags merge over the ambient ones, so a per-event tag cannot silently drop the deployment-wide ones.
	Tags map[string]string
}

// Breadcrumbs ride the Sentry {"values": []} envelope, not a bare array.
type breadcrumbs struct {
	Values []Breadcrumb `json:"values"`
}

type exceptions struct {
	Values []exceptionValue `json:"values"`
}

type exceptionValue struct {
	Type       string      `json:"type"`
	Value      string      `json:"value"`
	Mechanism  *mechanism  `json:"mechanism,omitempty"`
	Stacktrace *stacktrace `json:"stacktrace,omitempty"`
}

// A user-invoked capture is a handled capture (Sentry's default). The relay reads mechanism.handled for
// the unhandled badge.
type mechanism struct {
	Type    string `json:"type"`
	Handled bool   `json:"handled"`
}

type stacktrace struct {
	Frames []frame `json:"frames"`
}

type frame struct {
	Filename string `json:"filename,omitempty"`
	Function string `json:"function,omitempty"`
	Module   string `json:"module,omitempty"`
	Lineno   int    `json:"lineno,omitempty"`
	InApp    bool   `json:"in_app"`
}

func toException(err error, handled bool) *exceptions {
	value := "nil error"
	typ := "error"
	if err != nil {
		value = err.Error()
		typ = reflect.TypeOf(err).String()
	}

	ex := exceptionValue{
		Type:      typ,
		Value:     value,
		Mechanism: &mechanism{Type: "generic", Handled: handled},
	}
	if frames := captureStack(); len(frames) > 0 {
		ex.Stacktrace = &stacktrace{Frames: frames}
	}
	return &exceptions{Values: []exceptionValue{ex}}
}
