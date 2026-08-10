# frozen_string_literal: true

require "rbconfig"

module Condux
  # Builds the Sentry "exception" payload (type/value/mechanism/stacktrace) from an exception.
  module EventPayload
    RUBY_LIB_DIR = RbConfig::CONFIG["rubylibdir"]

    module_function

    def exception(error, handled: true)
      exception = {
        "type" => error.class.name,
        "value" => error.message.to_s,
        # The relay reads mechanism.handled for the unhandled badge. A direct capture_exception is a handled
        # capture; a framework integration reporting an uncaught exception passes handled: false.
        "mechanism" => { "type" => "generic", "handled" => handled },
      }
      frames = stack_frames(error)
      exception["stacktrace"] = { "frames" => frames } unless frames.empty?
      { "values" => [exception] }
    end

    # Ruby backtrace locations are innermost-first (the raise site first); reverse to oldest-first (raise
    # site last), the order the relay's fingerprinter and issue detail expect.
    def stack_frames(error)
      locations = error.backtrace_locations
      return [] unless locations

      locations.reverse.map do |loc|
        {
          "filename" => loc.path,
          "function" => loc.label,
          "lineno" => loc.lineno,
          "in_app" => in_app?(loc.path),
        }
      end
    end

    # Application frames drive grouping + the culprit; gems and the standard library are noise.
    def in_app?(path)
      return false if path.nil?

      !path.include?("/gems/") && !(RUBY_LIB_DIR && path.start_with?(RUBY_LIB_DIR))
    end
  end
end
