# frozen_string_literal: true

# Request isolation and per-event enrichment.
#
# The concurrency test runs real threads rather than asserting the mechanism in the abstract: the bug it
# exists to prevent only appears when two requests overlap, and a sequential test passes with a shared
# scope, proving nothing.

require "minitest/autorun"
require "json"
require_relative "../lib/condux"
require_relative "../lib/condux/rack"

class RequestScopeTest < Minitest::Test
  DSN = "http://pub123@relay.test/7"

  def setup
    Condux.clear_scope
    @bodies = []
    # Mutex-guarded because the concurrency test appends from several threads at once.
    @mutex = Mutex.new
    transport = lambda do |_url, _headers, body|
      @mutex.synchronize { @bodies << body }
      [202, {}]
    end
    Condux.init(dsn: DSN, transport: transport)
  end

  def teardown
    # Process-level state: leaking it would enrich (and so break) every later test's wire assertions.
    Condux.clear_scope
  end

  def events
    @mutex.synchronize { @bodies.map { |body| JSON.parse(body) } }
  end

  def test_process_scope_still_applies_with_no_request_active
    # Backward compatibility: a boot-time set_tag must behave exactly as before request scopes existed.
    Condux.set_tag("service", "billing")
    Condux.capture_message("hello")
    assert_equal({ "service" => "billing" }, events.last["tags"])
  end

  def test_request_scope_layers_over_process_scope
    Condux.set_tag("service", "billing")
    Condux.request_scope do
      Condux.set_tag("tenant", "acme")
      Condux.capture_message("inside")
    end
    assert_equal({ "service" => "billing", "tenant" => "acme" }, events.last["tags"])
  end

  def test_request_scope_does_not_outlive_the_request
    Condux.request_scope { Condux.set_user({ "id" => "u-1" }) }
    Condux.capture_message("after")
    # The leak in miniature: without the restore, the next event carries the previous request's user.
    refute events.last.key?("user")
  end

  def test_concurrent_threads_do_not_see_each_others_user
    barrier = Queue.new
    threads = %w[u-1 u-2].map do |id|
      Thread.new do
        Condux.request_scope do
          Condux.set_user({ "id" => id })
          # Both threads set their user before either captures, so a shared scope means whichever wrote
          # last wins for BOTH events.
          barrier << :ready
          sleep 0.01 while barrier.size < 2
          Condux.capture_message(id)
        end
      end
    end
    threads.each(&:join)

    by_message = events.to_h { |event| [event["message"], event["user"]["id"]] }
    assert_equal({ "u-1" => "u-1", "u-2" => "u-2" }, by_message)
  end

  def test_rack_reports_the_request_and_never_the_headers
    app = ->(_env) { raise "boom" }
    env = {
      "PATH_INFO" => "/checkout",
      "REQUEST_METHOD" => "POST",
      "QUERY_STRING" => "step=2",
      "HTTP_COOKIE" => "session=supersecret",
      "HTTP_AUTHORIZATION" => "Bearer tok"
    }

    assert_raises(RuntimeError) { Condux::Rack::CaptureExceptions.new(app).call(env) }

    event = events.last
    assert_equal({ "url" => "/checkout", "method" => "POST", "query_string" => "step=2" },
                 event["request"])
    serialized = JSON.generate(event)
    refute_includes serialized, "supersecret"
    refute_includes serialized, "Bearer"
  end

  def test_the_application_exception_still_propagates
    # The middleware captures inside a rescue before re-raising, so reporting must never replace it.
    app = ->(_env) { raise KeyError, "original" }
    assert_raises(KeyError) { Condux::Rack::CaptureExceptions.new(app).call({ "PATH_INFO" => "/x" }) }
  end
end
