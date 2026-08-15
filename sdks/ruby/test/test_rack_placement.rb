# frozen_string_literal: true

# The Rails placement guarantee.
#
# The middleware works on Rails only because config.middleware.use APPENDS, landing it inside
# ActionDispatch's ShowExceptions rather than above it. Above it there is nothing to catch: those
# middlewares convert a controller exception into a 500, so an outer wrapper sees no exception and
# reports nothing, silently. That is a property of where it sits, not of what it does, so it is pinned
# here rather than only described in a comment.
#
# Rails itself is not installed for the unit suite, so this models the two positions with a stand-in for
# ShowExceptions: a middleware that rescues and returns a 500 exactly as Rails does. The full stack is
# exercised against a real Rails app in the proving ground.

require "minitest/autorun"
require "json"
require_relative "../lib/condux"
require_relative "../lib/condux/rack"

class RackPlacementTest < Minitest::Test
  # Stands in for ActionDispatch::ShowExceptions: catches anything below it and answers 500.
  class ShowExceptions
    def initialize(app)
      @app = app
    end

    def call(env)
      @app.call(env)
    rescue StandardError
      [500, {}, ["error"]]
    end
  end

  FAILING_APP = ->(_env) { raise "boom" }

  def setup
    Condux.clear_scope
    @bodies = []
    Condux.init(dsn: "http://k@relay.test/1", transport: ->(_u, _h, body) { @bodies << body and [202, {}] })
  end

  def env
    { "PATH_INFO" => "/checkout", "REQUEST_METHOD" => "GET" }
  end

  def test_inside_the_exception_handler_it_reports
    # What config.middleware.use produces: Condux below ShowExceptions.
    stack = ShowExceptions.new(Condux::Rack::CaptureExceptions.new(FAILING_APP))

    status, = stack.call(env)

    assert_equal 500, status, "the app still gets its own error response"
    assert_equal 1, @bodies.length, "the exception was reported"
    assert_equal "/checkout", JSON.parse(@bodies.first).dig("request", "url")
  end

  def test_outside_the_exception_handler_it_reports_nothing
    # What insert_before would produce: Condux above ShowExceptions. This documents the failure rather
    # than endorsing it, and it is the exact shape that made the Flask WSGI middleware useless.
    stack = Condux::Rack::CaptureExceptions.new(ShowExceptions.new(FAILING_APP))

    status, = stack.call(env)

    assert_equal 500, status
    assert_empty @bodies, "nothing escapes ShowExceptions, so nothing above it can report"
  end
end
