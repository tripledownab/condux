package condux

import "reflect"

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
type event struct {
	EventID     string      `json:"event_id"`
	Timestamp   float64     `json:"timestamp"`
	Platform    string      `json:"platform"`
	Level       Level       `json:"level"`
	Environment string      `json:"environment,omitempty"`
	Release     string      `json:"release,omitempty"`
	Message     string      `json:"message,omitempty"`
	Exception   *exceptions `json:"exception,omitempty"`
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

func toException(err error) *exceptions {
	value := "nil error"
	typ := "error"
	if err != nil {
		value = err.Error()
		typ = reflect.TypeOf(err).String()
	}

	ex := exceptionValue{
		Type:      typ,
		Value:     value,
		Mechanism: &mechanism{Type: "generic", Handled: true},
	}
	if frames := captureStack(); len(frames) > 0 {
		ex.Stacktrace = &stacktrace{Frames: frames}
	}
	return &exceptions{Values: []exceptionValue{ex}}
}
