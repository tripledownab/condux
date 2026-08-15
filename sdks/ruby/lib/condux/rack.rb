# frozen_string_literal: true

require_relative "../condux"

module Condux
  module Rack
    # Rack middleware: report an uncaught exception (as unhandled) and re-raise, so the app's own error
    # handling still runs. Covers Rails and any Rack app.
    #
    #   # config.ru (Rack)
    #   require "condux/rack"
    #   use Condux::Rack::CaptureExceptions
    #
    #   # Rails (config/application.rb)
    #   config.middleware.use "Condux::Rack::CaptureExceptions"
    #
    # ON RAILS, USE `config.middleware.use` AND NOTHING ELSE.
    #
    # `use` APPENDS, which puts this at the very bottom of the stack, inside ActionDispatch's
    # ShowExceptions and DebugExceptions. That position is the whole reason it works: those two catch a
    # controller exception and turn it into a 500, so anything above them never sees the exception at
    # all and would report nothing, silently. Verified against a real Rails app, where this lands at
    # position 21 with ShowExceptions at 9.
    #
    # So `insert_before`, `insert_after` or `unshift` will move it above them and it will stop reporting
    # with no error to tell you. A future Rails release reordering its own stack could do the same, which
    # is why the position is pinned by a test rather than only described here.
    class CaptureExceptions
      def initialize(app)
        @app = app
      end

      def call(env)
        # A scope per request, so a set_user in a controller belongs to that request and cannot attach to
        # a concurrent one. Puma reuses threads, which is exactly why this has to be scoped rather than
        # left to process state.
        Condux.request_scope do
          @app.call(env)
        rescue StandardError => e # report anything the app raises, then re-raise
          Condux.capture_exception(e, handled: false, request: self.class.request_fields(env))
          raise
        end
      end

      # The request, in the Sentry store shape the relay parses.
      #
      # Deliberately only the path, method and query string. Rack puts headers in HTTP_* keys, including
      # HTTP_COOKIE and HTTP_AUTHORIZATION; the relay scrubs sensitive keys at ingest, but not sending
      # credentials at all is the stronger guarantee.
      def self.request_fields(env)
        {
          "url" => env["PATH_INFO"],
          "method" => env["REQUEST_METHOD"],
          "query_string" => env["QUERY_STRING"]
        }.reject { |_key, value| value.nil? || value.empty? }
      end
    end
  end
end
