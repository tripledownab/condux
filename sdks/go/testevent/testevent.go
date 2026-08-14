// Package testevent implements `condux-test-event`, which proves the pipeline end to end.
//
// An error monitor's failure mode is silence, and silence looks exactly like health. Run sends one
// info-level message through the real client and transport and reports the delivery outcome, so "did my
// DSN / network / relay work" is one command instead of waiting for a production error.
package testevent

import (
	"fmt"
	"io"

	condux "github.com/tripledownab/condux/sdks/go"
)

// Usage is printed whenever the arguments do not name a runnable command.
const Usage = "Usage: condux-test-event [--dsn <dsn>] [--message <text>]"

// Run sends the test event and returns the process exit code: 0 delivered, 1 delivery failed, 2 usage.
// dsn is the resolved DSN (flag first, then the environment) so the caller owns environment lookup.
func Run(dsn, message string, out, errOut io.Writer) int {
	if dsn == "" {
		fmt.Fprintf(errOut, "condux-test-event: no DSN. Pass --dsn <dsn> or set CONDUX_DSN. %s\n", Usage)
		return 2
	}

	client, err := condux.New(condux.Options{DSN: dsn, Environment: "condux-test"})
	if err != nil {
		fmt.Fprintf(errOut, "condux-test-event: %v. %s\n", err, Usage)
		return 2
	}

	if message == "" {
		message = "Condux test event"
	}
	result := client.CaptureMessage(message, condux.LevelInfo)
	if result.OK {
		fmt.Fprintf(out, "Delivered %q (%d attempt(s)). Check your project's issues list; a test message "+
			"appears as an info-level issue.\n", message, result.Attempts)
		return 0
	}

	reason := result.Error
	if reason == "" {
		reason = fmt.Sprintf("relay answered %d", result.Status)
	}
	fmt.Fprintf(errOut, "Delivery FAILED after %d attempt(s): %s. Check the DSN (Project settings -> DSN "+
		"keys) and that the ingest host is reachable.\n", result.Attempts, reason)
	return 1
}
