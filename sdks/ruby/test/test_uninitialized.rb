# frozen_string_literal: true

require "minitest/autorun"
require "stringio"
require_relative "../lib/condux"
require_relative "../lib/condux/rack"

# Capture before init must never raise.
#
# This is the sharpest edge in the SDK: the Rack middleware calls capture_exception *inside* a rescue
# before re-raising, so an SDK that raised when uninitialized would replace the application's own
# exception with the SDK's. An install that forgot init would then corrupt every error it was meant to
# observe. Capture warns once and drops the event instead.
class UninitializedCaptureTest < Minitest::Test
  def setup
    # Module-level state, set explicitly so this holds whatever order the suite runs in.
    Condux.instance_variable_set(:@client, nil)
    Condux.instance_variable_set(:@warned_uninitialized, false)
  end

  def teardown
    Condux.instance_variable_set(:@client, nil)
    Condux.instance_variable_set(:@warned_uninitialized, false)
  end

  # Kernel#warn writes to $stderr; swap it so the suite output stays clean and the warning is assertable.
  def capturing_stderr
    original = $stderr
    $stderr = StringIO.new
    yield
    $stderr.string
  ensure
    $stderr = original
  end

  def test_capture_message_returns_a_failure_instead_of_raising
    result = nil
    capturing_stderr { result = Condux.capture_message("dropped") }

    refute result.ok
    assert_equal 0, result.attempts
    assert_equal "not_initialized", result.error
  end

  def test_capture_exception_returns_a_failure_instead_of_raising
    result = nil
    capturing_stderr { result = Condux.capture_exception(ArgumentError.new("boom")) }

    refute result.ok
    assert_equal "not_initialized", result.error
  end

  def test_warns_once_however_many_events_are_dropped
    output = capturing_stderr do
      Condux.capture_message("first")
      Condux.capture_message("second")
      Condux.capture_exception(ArgumentError.new("third"))
    end

    assert_equal 1, output.scan("capture called before").length
  end

  def test_rack_middleware_propagates_the_apps_own_exception
    app = ->(_env) { raise KeyError, "the app's own failure" }
    middleware = Condux::Rack::CaptureExceptions.new(app)

    error = nil
    capturing_stderr { error = assert_raises(KeyError) { middleware.call({}) } }

    assert_equal "the app's own failure", error.message
  end
end
