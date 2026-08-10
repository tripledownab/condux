# frozen_string_literal: true

require "minitest/autorun"
require "json"
require_relative "../lib/condux"
require_relative "../lib/condux/rack"

# The Rack middleware reports an uncaught exception (as unhandled) and re-raises, exercised with a fake
# Rack app so no Rails is needed. A recording transport captures what reached the relay.
class RackMiddlewareTest < Minitest::Test
  def recorder
    bodies = []
    transport = ->(_url, _headers, body) { bodies << body and [202, {}] }
    Condux.init(dsn: "https://k@ingest.test/1", transport: transport)
    bodies
  end

  def test_reports_uncaught_exception_as_unhandled_and_reraises
    bodies = recorder
    app = ->(_env) { raise "rack boom" }
    middleware = Condux::Rack::CaptureExceptions.new(app)

    assert_raises(RuntimeError) { middleware.call({}) }

    exception = JSON.parse(bodies.last)["exception"]["values"][0]
    assert_equal "rack boom", exception["value"]
    assert_equal false, exception["mechanism"]["handled"]
  end

  def test_passes_a_successful_response_through_and_reports_nothing
    bodies = recorder
    app = ->(_env) { [200, {}, ["ok"]] }

    assert_equal [200, {}, ["ok"]], Condux::Rack::CaptureExceptions.new(app).call({})
    assert_empty bodies
  end
end
