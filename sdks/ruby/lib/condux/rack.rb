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
    #   # Rails (config/application.rb), the require at the top of the file
    #   require "condux/rack"
    #   config.middleware.use Condux::Rack::CaptureExceptions
    #
    # PASS THE CLASS, NOT ITS NAME AS A STRING. Rails builds each entry with `klass.new(app)`, so a
    # String argument aborts boot with `undefined method 'new' for an instance of String`. Rails
    # deprecated string middleware in 5.0 and removed the constantize in 5.1, so there is no supported
    # version where the string form works. The require is needed too: Bundler loads `condux`, which
    # does not define `Condux::Rack`, and without it boot fails on an uninitialized constant.
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
        rescue StandardError => e # report what the app got wrong, then re-raise whatever it was
          unless self.class.caller_caused?(e)
            Condux.capture_exception(e, handled: false, request: self.class.request_fields(env))
          end
          raise
        end
      end

      # True when the framework classifies this exception as the CALLER's mistake, not the app's.
      #
      # Rails keeps that classification in ActionDispatch::ExceptionWrapper.rescue_responses, a public
      # registry of exception class name to status. Asking it means there is no list of our own to keep
      # extending, and `config.action_dispatch.rescue_responses` merges into the same registry, so an
      # app's own classifications are honoured with no API from us.
      #
      # The registry defaults to :internal_server_error, so anything Rails does not recognise still
      # reports: the rule fails toward reporting rather than toward silence. Measured against Rails
      # 8.1.3.1, it answers 500 for an unknown class, an empty string and nil, and raises for none of
      # them, which is why there is no defensive rescue here to go stale.
      #
      # Bare Rack has no equivalent registry, so with ActionDispatch absent nothing is filtered and the
      # behaviour is exactly as before. See ADR-0044.
      def self.caller_caused?(error)
        return false unless defined?(ActionDispatch::ExceptionWrapper)

        status = ActionDispatch::ExceptionWrapper.status_code_for_exception(error.class.name)
        status.is_a?(Integer) && status >= 400 && status < 500
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
