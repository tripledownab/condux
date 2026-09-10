# frozen_string_literal: true

require "minitest/autorun"
require_relative "../lib/condux"

# Drives sdks/conformance/backoff.tsv, the retry schedule every Condux SDK owes. The cases live in that
# file rather than here so the seven transports assert against one artifact instead of seven readings of
# one sentence in a comment. See the file for why it is data and not prose.
class BackoffConformanceTest < Minitest::Test
  DSN = "https://testkey@ingest.test/proj-uuid"
  FIXTURE = File.expand_path("../../conformance/backoff.tsv", __dir__)

  # [attempt, status, retry-after or nil, expected milliseconds] for every row in the fixture.
  # Written for Ruby 2.6, the floor the gemspec declares, so no filter_map and no endless method.
  def cases
    File.readlines(FIXTURE, chomp: true).each_with_object([]) do |line, rows|
      text = line.strip
      next if text.empty? || text.start_with?("#")

      attempt, status, retry_after, expected_ms = text.split("\t")
      retry_after = { "<none>" => nil, "<empty>" => "" }.fetch(retry_after, retry_after)
      rows << [attempt.to_i, status.to_i, retry_after, expected_ms.to_i]
    end
  end

  # A fixture that failed to load reads exactly like one where every case passed, so the count is
  # asserted rather than assumed. A floor, so adding a case does not mean editing seven SDKs.
  def test_the_fixture_loaded
    assert_operator cases.length, :>=, 15, "#{FIXTURE} looks truncated"
  end

  def test_waits_exactly_as_long_as_the_fleet_contract_says
    cases.each do |attempt, status, retry_after, expected_ms|
      headers = retry_after.nil? ? {} : { "Retry-After" => retry_after }
      delays = []
      # One more retry than the attempt under test, so the sleep that follows it is recorded. The
      # scripted response repeats, so every attempt fails and the schedule runs to its end.
      client = Condux::Client.new(
        dsn: DSN, environment: nil, release: nil, max_retries: attempt + 1,
        transport: ->(_url, _headers, _body) { [status, headers] },
        sleep: ->(seconds) { delays << seconds }, clock: nil
      )

      client.capture_message("hi", Condux::Level::INFO)

      context = "attempt #{attempt}, status #{status}, Retry-After #{retry_after.inspect}"
      assert_equal attempt + 1, delays.length, context
      assert_equal expected_ms, (delays[attempt] * 1000).round, context
    end
  end
end
