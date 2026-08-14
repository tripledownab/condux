# frozen_string_literal: true

require_relative "condux/level"
require_relative "condux/send_result"
require_relative "condux/scope"
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
    # Configure the SDK with a project DSN (and optional testing hooks). Raises on a malformed DSN:
    # setup runs once at developer time, so a typo is worth failing loudly for.
    def init(dsn:, environment: nil, release: nil, max_retries: Client::DEFAULT_MAX_RETRIES,
             transport: nil, sleep: nil, clock: nil)
      @client = Client.new(dsn: dsn, environment: environment, release: release,
                           max_retries: max_retries, transport: transport, sleep: sleep, clock: clock)
    end

    # Report an exception as an error-level event, with its stack trace. Never raises on delivery failure.
    # Pass handled: false for an uncaught exception (a framework integration does this).
    def capture_exception(error, handled: true)
      client = active_client
      return not_initialized unless client

      client.capture_exception(error, handled: handled)
    end

    # Report a bare message event at the given level (default info).
    def capture_message(message, level: Level::INFO)
      client = active_client
      return not_initialized unless client

      client.capture_message(message, level)
    end

    # Attach the signed-in user (id/email/username) to subsequent events; nil clears.
    def set_user(user)
      Scope.user = user
    end

    # Attach a tag to subsequent events; a nil value removes it.
    def set_tag(key, value)
      Scope.set_tag(key, value)
    end

    # Attach a named context object to subsequent events; nil removes it.
    def set_context(name, context)
      Scope.set_context(name, context)
    end

    # Record a breadcrumb; the trail (newest last, capped) rides every subsequent event.
    def add_breadcrumb(message, category: nil, level: nil, type: nil, data: nil, timestamp: nil)
      Scope.add_breadcrumb(message, category: category, level: level, type: type, data: data,
                                    timestamp: timestamp)
    end

    # Reset all ambient enrichment (tests, or a full sign-out).
    def clear_scope
      Scope.clear
    end

    private

    # Reporting never raises — an error monitor that raises turns a handled error into an unhandled one in
    # exactly the code path where someone is already dealing with a failure. The Rack middleware reports
    # from inside a rescue, so raising here would replace the application's own exception with this one.
    # Warn once and drop the event instead.
    def active_client
      return @client if @client

      unless @warned_uninitialized
        @warned_uninitialized = true
        warn "Condux: capture called before Condux.init(dsn:); events are being dropped."
      end
      nil
    end

    def not_initialized
      SendResult.new(ok: false, attempts: 0, error: "not_initialized")
    end
  end
end
