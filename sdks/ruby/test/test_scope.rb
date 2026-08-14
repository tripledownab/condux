# frozen_string_literal: true

require "minitest/autorun"
require "json"
require_relative "../lib/condux"

# Enrichment-scope tests: the ambient user/tags/contexts/breadcrumbs the relay parses. The wire keys are
# asserted field by field, because the scope is only useful if it lands in the exact shape the relay's
# parser reads — and because an unenriched event must keep its current shape exactly (no new keys),
# which a shape-agnostic test would not catch.
class ScopeTest < Minitest::Test
  def setup
    Condux.clear_scope
    @bodies = []
    transport = ->(_url, _headers, body) { @bodies << body and [202, {}] }
    Condux.init(dsn: "https://k@ingest.test/1", transport: transport)
  end

  def teardown
    # Process-level state: leaking it would enrich (and so break) every later test's wire assertions.
    Condux.clear_scope
  end

  def last_event
    JSON.parse(@bodies.last)
  end

  def test_unenriched_event_carries_none_of_the_scope_keys
    Condux.capture_message("plain")

    %w[user tags contexts breadcrumbs].each { |key| refute_includes last_event.keys, key }
  end

  def test_enriched_event_carries_the_sentry_wire_shape
    Condux.set_user({ "id" => "42", "email" => "dev@example.test" })
    Condux.set_tag("plan", "team")
    Condux.set_context("device", { "model" => "laptop", "cores" => 8 })
    Condux.add_breadcrumb("/checkout", category: "navigation", level: "info")

    Condux.capture_message("after enrichment")

    event = last_event
    assert_equal({ "id" => "42", "email" => "dev@example.test" }, event["user"])
    assert_equal({ "plan" => "team" }, event["tags"])
    assert_equal({ "device" => { "model" => "laptop", "cores" => 8 } }, event["contexts"])
    # Breadcrumbs ride the Sentry {"values" => [...]} envelope, not a bare array.
    crumbs = event["breadcrumbs"]["values"]
    assert_equal 1, crumbs.length
    assert_equal "/checkout", crumbs[0]["message"]
    assert_equal "navigation", crumbs[0]["category"]
    assert_equal "info", crumbs[0]["level"]
    assert_kind_of Float, crumbs[0]["timestamp"] # epoch seconds, stamped for you
  end

  def test_scope_rides_an_exception_capture_too
    Condux.set_tag("plan", "business")

    begin
      raise ArgumentError, "boom"
    rescue ArgumentError => e
      Condux.capture_exception(e)
    end

    event = last_event
    assert_equal({ "plan" => "business" }, event["tags"])
    assert_equal "ArgumentError", event["exception"]["values"][0]["type"]
  end

  def test_nil_clears_user_tag_and_context
    Condux.set_user({ "id" => "42" })
    Condux.set_tag("plan", "team")
    Condux.set_context("device", { "model" => "laptop" })

    Condux.set_user(nil)
    Condux.set_tag("plan", nil)
    Condux.set_context("device", nil)
    Condux.capture_message("after clearing")

    %w[user tags contexts].each { |key| refute_includes last_event.keys, key }
  end

  def test_breadcrumb_trail_is_capped_dropping_the_oldest
    35.times { |index| Condux.add_breadcrumb("step-#{index}") }

    Condux.capture_message("after many steps")

    crumbs = last_event["breadcrumbs"]["values"]
    assert_equal 30, crumbs.length
    # Newest last, oldest dropped: steps 0-4 are gone.
    assert_equal "step-5", crumbs.first["message"]
    assert_equal "step-34", crumbs.last["message"]
  end

  def test_clear_scope_removes_everything
    Condux.set_user({ "id" => "42" })
    Condux.set_tag("plan", "team")
    Condux.add_breadcrumb("/checkout")

    Condux.clear_scope
    Condux.capture_message("after sign out")

    %w[user tags contexts breadcrumbs].each { |key| refute_includes last_event.keys, key }
  end
end
