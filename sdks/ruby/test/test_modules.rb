# frozen_string_literal: true

require "minitest/autorun"
require "json"
require "condux"

# Runtime dependency inventory tests (ADR-0041).
#
# The wire key is asserted directly, because the relay reads a top-level "modules" string map and a
# renamed or nested key would be dropped silently by the parser rather than rejected.
class TestModules < Minitest::Test
  DSN = "http://pub123@relay.test/7"

  def setup
    Condux::Modules.reset
    @sent = []
  end

  def init(send_modules: true)
    transport = lambda do |_url, _headers, body|
      @sent << JSON.parse(body)
      [202, {}]
    end
    Condux.init(dsn: DSN, transport: transport, sleep: ->(*) {}, send_modules: send_modules)
  end

  def test_an_event_carries_the_loaded_gems_under_the_sentry_key
    init
    Condux.capture_message("hi")

    modules = @sent.first["modules"]
    refute_nil modules, "an event should carry the inventory"
    # This suite has required condux and json, so both are activated by definition.
    assert modules.key?("json"), "expected a gem this test has actually loaded"
    modules.each do |name, version|
      assert_kind_of String, name
      assert_kind_of String, version
      refute_empty version
    end
  end

  # What makes the feature affordable: the server deduplicates a release's inventory to one row per
  # package per day, so repeating the map on every event spends bytes for nothing.
  def test_the_inventory_rides_the_first_event_then_waits_out_the_interval
    init
    3.times { |i| Condux.capture_message("m#{i}") }

    assert_equal 1, @sent.count { |e| e.key?("modules") }
  end

  # Repeating matters as much as skipping: the event carrying the inventory can be dropped by a rate
  # limit before anything parses it, so one attempt per process would lose that day's inventory.
  def test_the_inventory_rides_again_once_the_interval_elapses
    start = 1_000_000.0

    refute_empty Condux::Modules.fields(start)
    assert_empty Condux::Modules.fields(start + Condux::Modules::MODULES_INTERVAL_SECONDS - 1)
    refute_empty Condux::Modules.fields(start + Condux::Modules::MODULES_INTERVAL_SECONDS)
  end

  def test_send_modules_false_leaves_the_inventory_off_entirely
    init(send_modules: false)
    Condux.capture_message("hi")

    refute @sent.first.key?("modules")
  end

  # Collection is lazy for a reason, so the reason is asserted rather than only commented.
  #
  # Gem.loaded_specs reports ACTIVATED gems, not installed ones: in a bare interpreter it holds a
  # single entry, because nothing has been required yet. Collecting during init, which an application
  # calls early in boot, would therefore report almost nothing. What makes capture-time collection
  # correct is that collect reads the LIVE set every time rather than a snapshot taken at init.
  #
  # Asserted by adding a spec to Gem.loaded_specs directly rather than by requiring some library,
  # because which standard libraries are gems differs by Ruby version: on the 2.6 this was written
  # against, requiring "base64" activates nothing at all, so that test would have passed or failed for
  # reasons unrelated to the SDK.
  def test_collection_reads_the_live_set_rather_than_a_snapshot
    init
    refute Condux::Modules.collect.key?("condux-fake-gem")

    spec = Gem::Specification.new do |s|
      s.name = "condux-fake-gem"
      s.version = "9.9.9"
    end
    Gem.loaded_specs["condux-fake-gem"] = spec
    begin
      assert_equal "9.9.9", Condux::Modules.collect["condux-fake-gem"]
    ensure
      Gem.loaded_specs.delete("condux-fake-gem")
    end

    refute Condux::Modules.collect.key?("condux-fake-gem")
  end

  def test_entries_are_sorted_so_every_event_carries_the_same_order
    init
    Condux.capture_message("hi")

    names = @sent.first["modules"].keys
    assert_equal names.sort, names
  end
end
