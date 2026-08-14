// Command condux-test-event sends one test event to a Condux relay and reports whether it arrived.
//
//	go run github.com/tripledownab/condux/sdks/go/cmd/condux-test-event --dsn "https://key@host/project"
//	CONDUX_DSN="https://key@host/project" condux-test-event
package main

import (
	"flag"
	"os"

	"github.com/tripledownab/condux/sdks/go/testevent"
)

func main() {
	dsn := flag.String("dsn", os.Getenv("CONDUX_DSN"), "project DSN (defaults to CONDUX_DSN)")
	message := flag.String("message", "", "the message to send (defaults to a standard test message)")
	flag.Parse()

	os.Exit(testevent.Run(*dsn, *message, os.Stdout, os.Stderr))
}
