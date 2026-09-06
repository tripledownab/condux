# frozen_string_literal: true

require "minitest/autorun"
require "json"
require_relative "../lib/condux"
require_relative "../lib/condux/rack"

# ADR-0044: an exception the FRAMEWORK classifies as the caller's mistake is re-raised, not reported.
#
# WHAT THIS FILE CAN AND CANNOT PROVE. There is no Rails here, so it defines a stand-in for
# ActionDispatch::ExceptionWrapper. That stand-in is not a guess: real Rails 8.1.3.1 was measured first,
# where status_code_for_exception takes a class NAME, returns an Integer, answers 400 for
# ActionDispatch::Http::Parameters::ParseError, and answers 500 for an unknown name, an empty string and
# nil alike. So this file pins the RULE. Only a real Rails app can pin the integration, and one was run
# against this change.
#
# It is a separate file because defining that constant changes which branch the rest of the suite takes.

module ActionDispatch
  class ExceptionWrapper
    STATUSES = { "CallerFault" => 400, "RateLimited" => 429 }.freeze

    # Defaults to 500 like the real registry, which is what makes the rule fail toward reporting.
    def self.status_code_for_exception(name) = STATUSES.fetch(name, 500)
  end
end

CallerFault = Class.new(StandardError)
RateLimited = Class.new(StandardError)
AppFault = Class.new(StandardError)

class RackCallerCausedTest < Minitest::Test
  def recorder
    bodies = []
    transport = ->(_url, _headers, body) { bodies << body and [202, {}] }
    Condux.init(dsn: "https://k@ingest.test/1", transport: transport)
    bodies
  end

  # Every case also asserts the app still sees its own exception, which is the half that must not change.
  def run_through(error_class)
    bodies = recorder
    middleware = Condux::Rack::CaptureExceptions.new(->(_env) { raise error_class, "boom" })
    assert_raises(error_class) { middleware.call({}) }
    bodies
  end

  def test_an_app_fault_is_still_reported
    # The control, and it runs first on purpose: without it the absences below would pass just as well
    # against a broken transport, which is the exact shape that let this class of bug ship.
    bodies = run_through(AppFault)

    refute_empty bodies
    assert_equal "boom", JSON.parse(bodies.last)["exception"]["values"][0]["value"]
  end

  def test_a_400_is_reraised_but_not_reported
    assert_empty run_through(CallerFault)
  end

  def test_the_top_of_the_4xx_range_counts_too
    assert_empty run_through(RateLimited)
  end

  def test_the_range_is_bounded_so_a_5xx_still_reports
    refute Condux::Rack::CaptureExceptions.caller_caused?(AppFault.new)
  end
end
