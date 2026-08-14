# frozen_string_literal: true

require_relative "../condux"

module Condux
  # `condux test-event` — prove the pipeline end to end.
  #
  # An error monitor's failure mode is silence, and silence looks exactly like health. This sends one
  # info-level message through the real client and transport and reports the delivery outcome, so "did my
  # DSN / network / relay work" is one command instead of waiting for a production error.
  module TestEvent
    USAGE = "Usage: condux test-event [--dsn <dsn>] [--message <text>]"

    module_function

    # Runs the command; returns the process exit code (0 delivered, 1 failed, 2 usage).
    def run(argv, out: $stdout, err: $stderr)
      command = argv.first
      return usage(err, "unknown command '#{command}'") unless command == "test-event"

      dsn = argument(argv, "--dsn") || ENV.fetch("CONDUX_DSN", nil)
      return usage(err, "no DSN. Pass --dsn <dsn> or set CONDUX_DSN") if dsn.nil? || dsn.empty?

      begin
        Condux.init(dsn: dsn, environment: "condux-test")
      rescue ArgumentError, URI::InvalidURIError => e
        return usage(err, e.message)
      end

      report(Condux.capture_message(argument(argv, "--message") || "Condux test event"), out, err)
    end

    def report(result, out, err)
      message_count = "#{result.attempts} attempt#{result.attempts == 1 ? "" : "s"}"
      if result.ok
        out.puts "Delivered (#{message_count}). Check your project's issues list; a test message " \
                 "appears as an info-level issue."
        return 0
      end

      err.puts "Delivery FAILED after #{message_count}: #{result.error || "relay answered #{result.status}"}. " \
               "Check the DSN (Project settings -> DSN keys) and that the ingest host is reachable."
      1
    end

    def argument(argv, name)
      index = argv.index(name)
      index && argv[index + 1]
    end

    def usage(err, reason)
      err.puts "condux: #{reason}. #{USAGE}"
      2
    end
  end
end
