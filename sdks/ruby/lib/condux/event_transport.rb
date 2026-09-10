# frozen_string_literal: true

require "net/http"
require "uri"
require_relative "send_result"

module Condux
  # Delivers a serialized event to the relay, retrying transient failures (429 / 5xx / network) with
  # capped exponential backoff, honoring Retry-After on a 429. Never raises — returns a SendResult.
  class EventTransport
    BASE_BACKOFF_MS = 200.0
    MAX_BACKOFF_MS = 30_000.0

    def initialize(store_url, public_key, max_retries, transport, sleep)
      @store_url = store_url
      @public_key = public_key
      @max_retries = max_retries
      @transport = transport || method(:net_http_send)
      @sleep = sleep || ->(seconds) { Kernel.sleep(seconds) }
    end

    def send_event(body)
      last_status = nil
      last_error = nil
      attempt = 0
      while attempt <= @max_retries
        status, headers, error = attempt_send(body)
        if error
          last_error = error
        else
          last_status = status
          last_error = nil
          return SendResult.new(ok: true, attempts: attempt + 1, status: status) if (200..299).cover?(status)
          return SendResult.new(ok: false, attempts: attempt + 1, status: status) unless retriable?(status)
        end

        break if attempt == @max_retries

        @sleep.call(backoff_seconds(attempt, status, headers))
        attempt += 1
      end

      SendResult.new(ok: false, attempts: @max_retries + 1, status: last_status, error: last_error)
    end

    private

    def attempt_send(body)
      headers = { "Content-Type" => "application/json", "x-condux-auth" => @public_key }
      status, response_headers = @transport.call(@store_url, headers, body)
      [status, response_headers || {}, nil]
    rescue StandardError => e # a genuine network failure — retry; a failed send must never crash the caller
      message = e.message.to_s
      [nil, {}, message.empty? ? e.class.name : message]
    end

    # 429 (rate limited) and 5xx are worth retrying; other 4xx (bad DSN / payload) are not.
    def retriable?(status)
      status == 429 || status >= 500
    end

    # Honor Retry-After (seconds) on a 429, else exponential backoff. Both paths are capped at
    # MAX_BACKOFF_MS, at ONE return so a later branch cannot route past it: the SDK holds the caller's
    # thread while it waits, so bounding that wait is its own obligation and not the relay's to set.
    # Returns seconds.
    def backoff_seconds(attempt, status, headers)
      wanted_ms = BASE_BACKOFF_MS * (2**attempt)
      if status == 429
        retry_after = header(headers, "retry-after")
        if retry_after && !retry_after.strip.empty?
          seconds = begin
            Float(retry_after)
          rescue ArgumentError, TypeError
            nil
          end
          wanted_ms = [seconds, 0.0].max * 1000.0 if seconds
        end
      end
      [wanted_ms, MAX_BACKOFF_MS].min / 1000.0
    end

    def header(headers, name)
      target = name.downcase
      headers.each { |key, value| return value if key.to_s.downcase == target }
      nil
    end

    def net_http_send(url, headers, body)
      uri = URI(url)
      http = Net::HTTP.new(uri.host, uri.port)
      http.use_ssl = uri.scheme == "https"
      request = Net::HTTP::Post.new(uri)
      headers.each { |key, value| request[key] = value }
      request.body = body
      response = http.request(request)
      [response.code.to_i, response]
    end
  end
end
