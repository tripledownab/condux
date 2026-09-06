# frozen_string_literal: true

module Condux
  # The runtime dependency inventory that rides events (ADR-0041): which gem versions are actually
  # loaded, as opposed to which ones a Gemfile declares. The relay indexes this so a security advisory
  # can be answered with "and you are running 2.3.0 in production" rather than only "your lockfile
  # says so".
  #
  # <b>This one is collected lazily, unlike the other SDKs, and that is not an inconsistency.</b>
  # Gem.loaded_specs reports the gems that have been ACTIVATED, not the ones installed. Measured in a
  # bare interpreter it holds a single entry, because nothing has been required yet. Collecting during
  # Condux.init, which an application typically calls early in boot, would therefore report almost
  # nothing. Collecting at capture time reports what was genuinely loaded by the moment the error
  # happened, which is both correct and the truest reading of what this feature claims to measure. It
  # costs nothing: loaded_specs is an in-memory hash, with no filesystem behind it.
  module Modules
    # The most entries carried on one event. The cap applies after sorting, so which entries survive is
    # stable across events rather than varying with hash order: the server sees one consistent set
    # instead of a shifting sample.
    MAX_MODULES = 1000

    # How long to wait before repeating the inventory on another event.
    #
    # This is what makes the feature affordable. The server deduplicates a release's inventory down to
    # one row per package per day, so attaching the whole map to every event would spend bytes for
    # nothing. Repeating on an interval rather than sending once keeps the robustness that every-event
    # buys: the event carrying the inventory can be dropped by a rate limit or a quota rejection
    # before anything parses it, so a single attempt per process would lose that day's inventory.
    MODULES_INTERVAL_SECONDS = 15 * 60

    module_function

    # The loaded gems as a name to version hash, empty when they cannot be read.
    #
    # Never raises. This runs inside capture, which is already handling somebody's error, so a broken
    # spec costs that entry and nothing else.
    def collect
      found = {}
      Gem.loaded_specs.each do |name, spec|
        version = spec.version.to_s
        found[name.to_s] = version if name && !name.to_s.empty? && !version.empty?
      rescue StandardError
        # One unreadable spec is not a reason to report nothing about the rest.
        next
      end
      found
    rescue StandardError, NameError
      # No RubyGems at all (a packaged binary, a trimmed runtime). Unknown is the honest answer.
      {}
    end

    # The inventory's contribution to an event: the full map on the first event and then at most once
    # per MODULES_INTERVAL_SECONDS, and an empty hash otherwise, so an event that carries nothing keeps
    # its exact previous wire shape.
    def fields(now)
      return {} if @last_attached_at && (now - @last_attached_at) < MODULES_INTERVAL_SECONDS

      loaded = collect
      return {} if loaded.empty?

      @last_attached_at = now
      { "modules" => loaded.sort.first(MAX_MODULES).to_h }
    end

    # Reset the interval. Tests only; a process loads its gems once.
    def reset
      @last_attached_at = nil
    end
  end
end
