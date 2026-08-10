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
    class CaptureExceptions
      def initialize(app)
        @app = app
      end

      def call(env)
        @app.call(env)
      rescue StandardError => e # report anything the app raises, then re-raise
        Condux.capture_exception(e, handled: false)
        raise
      end
    end
  end
end
