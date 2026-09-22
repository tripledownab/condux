#!/usr/bin/env bash
# What belongs here: the one check that decides whether a deployment is actually serving. Run on the
# host after a deploy, through the edge, once per public host. Every environment runs THIS file, so
# there is one statement of what "serving" means rather than one per workflow.
#
# Usage:  health-gate.sh <label>=<url> [<label>=<url> ...]
# Exit:   0 when every URL answered ready, 1 otherwise. Each result is printed either way.
#
# TWO THINGS ARE LOAD-BEARING, and both are here because a gate without them reported a deploy as
# healthy while it was not.
#
# 1. IT ASSERTS ON THE BODY, NOT THE STATUS CODE. An edge that answers every path from the wrong
#    handler returns 200 while the service behind it is unreachable. A gate reading %{http_code}
#    agrees with that instead of catching it, and calls a deploy healthy while events are accepted
#    and discarded.
#
# 2. IT TAKES EVERY PUBLIC HOST, not just the one a browser uses. A dashboard answering is not
#    evidence about an ingest endpoint on a different name and a different certificate. The half
#    nobody probes is the half whose outage loses data instead of blocking a page.
#
# It also reports a failed TLS verification apart from every other failure. That is not a lesson, it
# is a diagnosis: curl refuses a chain it cannot verify, so the symptom is an empty body, which reads
# as "the service is down" and sends someone to look in the wrong place.
#
# `-e` is deliberately absent. Both functions below signal by RETURN CODE, and every caller reads it,
# so `-e` would abort the run at the first unhealthy target instead of reporting the rest.
set -uo pipefail

ATTEMPTS="${HEALTH_GATE_ATTEMPTS:-30}"
SLEEP_SECONDS="${HEALTH_GATE_SLEEP:-5}"

# The token the readiness contract defines. Deliberately NOT the per-store booleans beside it: those
# exist so an operator can see WHICH dependency is down, and a gate that read them would fail a deploy
# over something the deploy did not change.
READY_TOKEN='"status":"ready"'

# Whitespace is stripped before matching so the token cannot depend on how the JSON is formatted. The
# bodies are compact today and nothing pins that, and a gate that fails every healthy deploy after a
# formatting change is one somebody switches off.
probe() { # probe <url>; prints "<verify> <compacted body>"
  local out verify body
  out="$(curl -sS --connect-timeout 5 --max-time 10 -w '\n%{ssl_verify_result}' "$1" 2>/dev/null || true)"
  verify="${out##*$'\n'}"
  body="${out%$'\n'*}"
  printf '%s %s' "${verify:-none}" "$(printf '%s' "$body" | tr -d ' \n\r\t')"
}

gate() { # gate <label> <url>
  local result verify compacted
  compacted=""
  for _ in $(seq 1 "$ATTEMPTS"); do
    result="$(probe "$2")"
    verify="${result%% *}"
    compacted="${result#* }"

    # Asked BEFORE the body, and that ordering is the whole of why this check works at all. curl
    # verifies by default, so a chain it rejects yields no body: fold this test inside the ready
    # branch and it can never run, because a bad chain never gets there. Not retried either, because
    # a certificate the client will not accept does not become acceptable in five seconds.
    #
    # Only a failed verification is non-zero. A refused connection, a DNS failure and a plain http
    # URL all report 0, so this cannot misfire on them. Measured against each.
    if [ "$verify" != "0" ] && [ "$verify" != "none" ]; then
      echo "$1: TLS chain did not verify at $2 (curl ssl_verify_result $verify)"
      return 1
    fi

    case "$compacted" in
      *"$READY_TOKEN"*) echo "$1: ready"; return 0 ;;
    esac
    sleep "$SLEEP_SECONDS"
  done
  echo "$1: NOT READY at $2, last body was: $(printf '%.140s' "${compacted:-<no response>}")"
  return 1
}

if [ "$#" -eq 0 ]; then
  echo "health-gate: no targets given; pass at least one <label>=<url>" >&2
  exit 2
fi

status=0
for target in "$@"; do
  case "$target" in
    *=*) ;;
    *) echo "health-gate: '$target' is not <label>=<url>" >&2; exit 2 ;;
  esac

  url="${target#*=}"
  # http(s) ONLY, and this is not pedantry about input. curl speaks file:// too, so without this the
  # gate reports "ready" for a local file that happens to contain the token: a green light from
  # something that is not a service at all, produced by the one tool whose entire job is refusing
  # false greens. Measured before the check existed. Refused rather than coerced, because a caller
  # that meant a URL and typed something else should be told, not guessed at.
  case "$url" in
    http://*|https://*) ;;
    *) echo "health-gate: '$url' is not an http(s) URL" >&2; exit 2 ;;
  esac

  gate "${target%%=*}" "$url" || status=1
done
exit "$status"
