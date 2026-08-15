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

    # +request+ (url/method/query_string) and +tags+ describe this one event. They are passed here
    # rather than set on the scope because scope state outlives the call: on a server handling requests
    # concurrently, request detail set ambiently attaches to whichever event is captured next, which may
    # belong to a different request.
    def capture_exception(error, handled: true, request: nil, tags: nil)
      dispatch(level: Level::ERROR, exception: EventPayload.exception(error, handled: handled),
               request: request, tags: tags)
    end

    def capture_message(message, level, request: nil, tags: nil)
      dispatch(level: level, message: message, request: request, tags: tags)
    end

    private

    def dispatch(level:, message: nil, exception: nil, request: nil, tags: nil)
      event = {
        "event_id" => SecureRandom.hex(16), # 32 lowercase hex, the Sentry event_id shape
        "timestamp" => @clock.call.to_f,    # epoch seconds, the store convention
        "platform" => "ruby",
        "level" => level,
      }.merge(Scope.fields)
      event["request"] = request if request && !request.empty?
      # Merged over the ambient tags rather than replacing them, so a per-event tag cannot silently drop
      # the deployment-wide ones.
      event["tags"] = (event["tags"] || {}).merge(tags) if tags && !tags.empty?
      event["environment"] = @environment if @environment
      event["release"] = @release if @release
      event["message"] = message if message
      event["exception"] = exception if exception
      @transport.send_event(JSON.generate(event))
    end
  end
end
