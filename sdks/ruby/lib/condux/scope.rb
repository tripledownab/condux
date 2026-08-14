# frozen_string_literal: true

module Condux
  # Ambient event enrichment: who the user is, which tags and contexts apply, and the breadcrumb trail
  # leading up to an error. Set once (or as the app's state changes) and every subsequent event carries
  # it — the first triage questions ("which customer, which plan, what did they do last") answered
  # without threading anything through capture calls. The relay already scrubs all of these at ingest
  # and derives the pseudonymous users-affected key from the user fields.
  module Scope
    # Newest trail wins: a long-lived process drops the oldest crumbs rather than growing without bound.
    MAX_BREADCRUMBS = 30

    @mutex = Mutex.new
    @user = nil
    @tags = {}
    @contexts = {}
    @breadcrumbs = []

    class << self
      # Attach the signed-in user (id/email/username) to subsequent events; nil clears.
      def user=(user)
        @mutex.synchronize { @user = user&.transform_keys(&:to_s) }
      end

      # Attach a tag to subsequent events; a nil value removes it.
      def set_tag(key, value)
        @mutex.synchronize do
          if value.nil?
            @tags.delete(key.to_s)
          else
            @tags[key.to_s] = value
          end
        end
      end

      # Attach a named context object to subsequent events; nil removes it.
      def set_context(name, context)
        @mutex.synchronize do
          if context.nil?
            @contexts.delete(name.to_s)
          else
            @contexts[name.to_s] = context.transform_keys(&:to_s)
          end
        end
      end

      # Record a breadcrumb; the trail (newest last, capped) rides every subsequent event.
      def add_breadcrumb(message, category: nil, level: nil, type: nil, data: nil, timestamp: nil)
        crumb = { "message" => message, "timestamp" => timestamp || Time.now.to_f }
        crumb["category"] = category if category
        crumb["level"] = level if level
        crumb["type"] = type if type
        crumb["data"] = data if data

        @mutex.synchronize do
          @breadcrumbs << crumb
          @breadcrumbs.shift while @breadcrumbs.length > MAX_BREADCRUMBS
        end
      end

      # Reset all ambient state (tests, or a full sign-out).
      def clear
        @mutex.synchronize do
          @user = nil
          @tags = {}
          @contexts = {}
          @breadcrumbs = []
        end
      end

      # The scope's contribution to an event, holding only the keys that are actually set so an
      # unenriched event keeps its exact wire shape. Breadcrumbs use the Sentry {"values" => []} envelope.
      def fields
        @mutex.synchronize do
          fields = {}
          fields["user"] = @user.dup if @user
          fields["tags"] = @tags.dup unless @tags.empty?
          fields["contexts"] = @contexts.dup unless @contexts.empty?
          fields["breadcrumbs"] = { "values" => @breadcrumbs.dup } unless @breadcrumbs.empty?
          fields
        end
      end
    end
  end
end
