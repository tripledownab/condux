# frozen_string_literal: true

require "minitest/autorun"
require "socket"
require "stringio"
require_relative "../lib/condux/test_event"

# Tests for `condux test-event`. The exit code is the contract a CI script branches on, so assert the
# code, not just the output. Delivery runs against a real local HTTP server: the command's whole purpose
# is proving the actual transport works, and a fake transport here would test nothing the rest of the
# suite does not.
class TestEventCommandTest < Minitest::Test
  def run_command(argv, env: {})
    original = ENV.fetch("CONDUX_DSN", nil)
    ENV["CONDUX_DSN"] = env.fetch("CONDUX_DSN", "")
    [Condux::TestEvent.run(argv, out: StringIO.new, err: StringIO.new)]
  ensure
    original.nil? ? ENV.delete("CONDUX_DSN") : ENV["CONDUX_DSN"] = original
  end

  def test_no_dsn_is_a_usage_error
    assert_equal [2], run_command(["test-event"])
  end

  def test_unknown_command_is_a_usage_error
    assert_equal [2], run_command(["frobnicate"])
  end

  def test_malformed_dsn_is_a_usage_error
    assert_equal [2], run_command(["test-event", "--dsn", "https://ingest.test/1"])
  end

  # A raw TCPServer rather than WEBrick, which stopped being a default gem in Ruby 3.0 and so is not
  # importable on a bare CI runner.
  def test_delivery_to_a_live_relay_exits_zero
    server = TCPServer.new("127.0.0.1", 0)
    thread = Thread.new do
      socket = server.accept
      nil while (line = socket.gets) && !line.strip.empty? # drain the request line + headers
      socket.write("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
      socket.close
    end
    begin
      assert_equal [0], run_command(["test-event", "--dsn", "http://key@127.0.0.1:#{server.addr[1]}/1"])
    ensure
      thread.join(5)
      server.close
    end
  end

  def test_a_refused_relay_exits_one
    # Port 9 (discard) refuses immediately, so the retry loop gives up without waiting on a timeout.
    assert_equal [1], run_command(["test-event", "--dsn", "http://key@127.0.0.1:9/1", "--message", "nope"])
  end
end
