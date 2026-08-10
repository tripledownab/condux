# frozen_string_literal: true

require_relative "condux/level"
require_relative "condux/send_result"
require_relative "condux/client"

# Condux SDK for Ruby — report errors to a Condux relay.
#
# Emits the Sentry "store" wire shape (event_id, timestamp, level, exception.values[]) so the relay
# normalizes it exactly like an official Sentry SDK: swap the DSN and it works. Delivery is resilient
# (429 / 5xx / network failures retry with backoff, honoring Retry-After) and never raises — a failed send
# returns a SendResult you can inspect. The transport and sleep are injectable so backoff is exercised with
# no real network or timers. Inspired by common SDK transports, implemented fresh.
module Condux
  class << self
    # Configure the SDK with a project DSN (and optional testing hooks).
    def init(dsn:, environment: nil, release: nil, max_retries: Client::DEFAULT_MAX_RETRIES,
             transport: nil, sleep: nil, clock: nil)
      @client = Client.new(dsn: dsn, environment: environment, release: release,
                           max_retries: max_retries, transport: transport, sleep: sleep, clock: clock)
    end

    # Report an exception as an error-level event, with its stack trace. Never raises on delivery failure.
    # Pass handled: false for an uncaught exception (a framework integration does this).
    def capture_exception(error, handled: true)
      require_client.capture_exception(error, handled: handled)
    end

    # Report a bare message event at the given level (default info).
    def capture_message(message, level: Level::INFO)
      require_client.capture_message(message, level)
    end

    private

    def require_client
      raise "Condux not initialized — call Condux.init(dsn:) first" unless @client

      @client
    end
  end
end
