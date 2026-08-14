# frozen_string_literal: true

require "securerandom"
require "json"
require_relative "level"
require_relative "dsn"
require_relative "event_payload"
require_relative "event_transport"
require_relative "scope"

module Condux
  # A configured reporter. Cheap to hold for the process lifetime; safe for concurrent use.
  class Client
    DEFAULT_MAX_RETRIES = 3

    def initialize(dsn:, environment:, release:, max_retries:, transport:, sleep:, clock:)
      parsed = Dsn.parse(dsn)
      @transport = EventTransport.new(parsed.store_url, parsed.public_key, max_retries, transport, sleep)
      @environment = environment
      @release = release
      @clock = clock || -> { Time.now }
    end

    def capture_exception(error, handled: true)
      dispatch(level: Level::ERROR, exception: EventPayload.exception(error, handled: handled))
    end

    def capture_message(message, level)
      dispatch(level: level, message: message)
    end

    private

    def dispatch(level:, message: nil, exception: nil)
      event = {
        "event_id" => SecureRandom.hex(16), # 32 lowercase hex, the Sentry event_id shape
        "timestamp" => @clock.call.to_f,    # epoch seconds, the store convention
        "platform" => "ruby",
        "level" => level,
      }.merge(Scope.fields)
      event["environment"] = @environment if @environment
      event["release"] = @release if @release
      event["message"] = message if message
      event["exception"] = exception if exception
      @transport.send_event(JSON.generate(event))
    end
  end
end
