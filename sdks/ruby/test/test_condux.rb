# frozen_string_literal: true

require "minitest/autorun"
require "json"
require_relative "../lib/condux"

# Proves the emitted JSON is the Sentry store wire shape (field by field) and the transport is resilient,
# with an injected transport + sleep so there is no real network or waiting. Built like the JS/Python suites.
class ConduxTest < Minitest::Test
  DSN = "https://testkey@ingest.test/proj-uuid"

  # Replays a scripted sequence of responses (the last repeats), records requests + bodies + backoff delays.
  # A response {error: e} makes the transport raise, simulating a network failure.
  class Recorder
    attr_reader :bodies, :requests, :delays

    def initialize(responses)
      @responses = responses
      @bodies = []
      @requests = []
      @delays = []
    end

    def transport
      lambda do |url, headers, body|
        @requests << { url: url, headers: headers }
        @bodies << body
        resp = @responses[[@requests.length - 1, @responses.length - 1].min]
        raise resp[:error] if resp[:error]

        [resp[:status], resp[:headers] || {}]
      end
    end

    def sleep_fn
      ->(seconds) { @delays << seconds }
    end
  end

  def build(responses, max_retries: 3, environment: "test", release: "1.2.3")
    rec = Recorder.new(responses)
    client = Condux::Client.new(dsn: DSN, environment: environment, release: release,
                                max_retries: max_retries, transport: rec.transport, sleep: rec.sleep_fn, clock: nil)
    [client, rec]
  end

  def test_capture_exception_emits_sentry_store_shape
    client, rec = build([{ status: 200 }])
    result = begin
      raise ArgumentError, "boom from ruby"
    rescue StandardError => e
      client.capture_exception(e)
    end

    assert result.ok
    event = JSON.parse(rec.bodies[0])
    assert_match(/\A[0-9a-f]{32}\z/, event["event_id"])
    assert_kind_of Float, event["timestamp"]
    assert_equal "ruby", event["platform"]
    assert_equal "error", event["level"]
    assert_equal "test", event["environment"]
    assert_equal "1.2.3", event["release"]

    ex = event["exception"]["values"][0]
    assert_equal "ArgumentError", ex["type"]
    assert_equal "boom from ruby", ex["value"]
    assert_equal "generic", ex["mechanism"]["type"]
    assert_equal true, ex["mechanism"]["handled"]

    frames = ex["stacktrace"]["frames"]
    refute_empty frames
    top = frames.last # oldest-first: the raise site is last, and it is this in-app test method
    assert_includes top["function"], "test_capture_exception"
    assert_equal true, top["in_app"]

    assert_equal "testkey", rec.requests[0][:headers]["x-condux-auth"]
    assert_equal "https://ingest.test/api/proj-uuid/store/", rec.requests[0][:url]
  end

  def test_capture_exception_can_mark_unhandled
    # A framework integration reports uncaught exceptions with handled: false, driving the unhandled badge.
    client, rec = build([{ status: 200 }])
    begin
      raise ArgumentError, "uncaught"
    rescue StandardError => e
      client.capture_exception(e, handled: false)
    end

    assert_equal false, JSON.parse(rec.bodies[0])["exception"]["values"][0]["mechanism"]["handled"]
  end

  def test_capture_message_emits_message_without_exception
    client, rec = build([{ status: 200 }])
    client.capture_message("disk almost full", Condux::Level::WARNING)

    event = JSON.parse(rec.bodies[0])
    assert_equal "warning", event["level"]
    assert_equal "disk almost full", event["message"]
    refute event.key?("exception")
  end

  def test_retries_429_honoring_retry_after
    client, rec = build([{ status: 429, headers: { "Retry-After" => "3" } }, { status: 200 }])
    result = client.capture_message("hi", Condux::Level::INFO)

    assert result.ok
    assert_equal 2, result.attempts
    assert_equal [3.0], rec.delays
  end

  def test_retries_5xx_with_capped_exponential_backoff
    client, rec = build([{ status: 503 }, { status: 503 }, { status: 200 }])
    result = client.capture_message("hi", Condux::Level::INFO)

    assert result.ok
    assert_equal 3, result.attempts
    assert_equal [0.2, 0.4], rec.delays
  end

  def test_does_not_retry_a_client_error
    client, rec = build([{ status: 400 }])
    result = client.capture_message("hi", Condux::Level::INFO)

    refute result.ok
    assert_equal 1, result.attempts
    assert_equal 400, result.status
    assert_empty rec.delays
  end

  def test_reports_a_network_error_without_raising
    client, = build([{ error: RuntimeError.new("connection refused") }], max_retries: 1)
    result = client.capture_message("hi", Condux::Level::INFO)

    refute result.ok
    assert_equal 2, result.attempts
    assert_nil result.status
    refute_nil result.error
  end

  def test_rejects_a_malformed_dsn
    assert_raises(ArgumentError) do
      Condux::Client.new(dsn: "https://ingest.test/no-key", environment: nil, release: nil,
                         max_retries: 3, transport: ->(*) {}, sleep: ->(*) {}, clock: nil)
    end
  end
end
