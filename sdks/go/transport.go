package condux

import (
	"bytes"
	"io"
	"math"
	"net/http"
	"strconv"
	"strings"
	"time"
)

const (
	baseBackoff = 200 * time.Millisecond
	maxBackoff  = 30 * time.Second
)

// send POSTs the serialized event to the relay, retrying transient failures with backoff. It never panics
// — it returns a SendResult describing the outcome.
func (c *Client) send(body []byte) SendResult {
	var lastStatus int
	var lastError string

	for attempt := 0; attempt <= c.maxRetries; attempt++ {
		status, retryAfter, err := c.post(body)
		if err != nil {
			lastError = err.Error()
		} else {
			lastStatus = status
			lastError = ""
			if status >= 200 && status < 300 {
				return SendResult{OK: true, Attempts: attempt + 1, Status: status}
			}
			if !isRetriable(status) {
				return SendResult{OK: false, Attempts: attempt + 1, Status: status}
			}
		}

		if attempt == c.maxRetries {
			break
		}
		c.sleep(backoff(attempt, status, retryAfter))
	}

	return SendResult{OK: false, Attempts: c.maxRetries + 1, Status: lastStatus, Error: lastError}
}

func (c *Client) post(body []byte) (status int, retryAfter string, err error) {
	req, err := http.NewRequest(http.MethodPost, c.storeURL, bytes.NewReader(body))
	if err != nil {
		return 0, "", err
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("x-condux-auth", c.publicKey)

	resp, err := c.http.Do(req)
	if err != nil {
		return 0, "", err
	}
	defer resp.Body.Close()
	_, _ = io.Copy(io.Discard, resp.Body)
	return resp.StatusCode, resp.Header.Get("Retry-After"), nil
}

// 429 (rate limited) and 5xx are worth retrying; other 4xx (bad DSN, bad payload) are not. A network error
// (status 0) is also retried.
func isRetriable(status int) bool {
	return status == 0 || status == http.StatusTooManyRequests || status >= 500
}

// backoff honors Retry-After (seconds) on a 429, else exponential backoff. Both paths are capped at
// maxBackoff, at ONE return so a later branch cannot route past it: the SDK holds the caller's
// goroutine while it waits, so bounding that wait is its own obligation and not the relay's to set.
func backoff(attempt, status int, retryAfter string) time.Duration {
	wantedMs := float64(baseBackoff/time.Millisecond) * math.Pow(2, float64(attempt))
	if status == http.StatusTooManyRequests && strings.TrimSpace(retryAfter) != "" {
		// A negative value is clamped rather than discarded, so a sender merely wrong about the sign is
		// read as asking to retry now. What IS discarded is anything that is not a finite number:
		// ParseFloat returns +Inf for "Infinity" and NaN for "NaN", both with a nil error, and a
		// Duration built from either saturates rather than meaning anything.
		seconds, err := strconv.ParseFloat(strings.TrimSpace(retryAfter), 64)
		if err == nil && !math.IsInf(seconds, 0) && !math.IsNaN(seconds) {
			wantedMs = math.Max(0, seconds) * 1000
		}
	}
	capped := math.Min(wantedMs, float64(maxBackoff/time.Millisecond))
	return time.Duration(capped) * time.Millisecond
}
