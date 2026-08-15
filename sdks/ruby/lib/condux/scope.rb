# frozen_string_literal: true

module Condux
  # Ambient event enrichment: who the user is, which tags and contexts apply, and the breadcrumb trail
  # leading up to an error. Set once (or as the app's state changes) and every subsequent event carries
  # it — the first triage questions ("which customer, which plan, what did they do last") answered
  # without threading anything through capture calls. The relay already scrubs all of these at ingest
  # and derives the pseudonymous users-affected key from the user fields.
  # There are two layers, and the distinction is the whole point:
  #
  #   Process state, set at boot and shared by everything. Right for facts about the deployment.
  #   A request scope, active only inside Scope.request. Right for facts about one request.
  #
  # Without the second, set_user in a Rails controller is a cross-request leak: Puma serves requests
  # concurrently, so one request's user would attach to another request's error. That is worse than
  # reporting no user, because it is confidently wrong and points an investigation at the wrong customer.
  # The Rack middleware opens a scope per request, so a set_user in a controller stays in that request.
  module Scope
    # Newest trail wins: a long-lived process drops the oldest crumbs rather than growing without bound.
    MAX_BREADCRUMBS = 30

    # Thread.current[] is FIBER-local in Ruby, unlike thread_variable_get which is thread-local. Fiber
    # local is what this wants: it isolates Puma's threads and also Falcon's fibers, where several
    # requests share one thread and a thread-local would let them see each other's user.
    STATE_KEY = :condux_request_scope

    @mutex = Mutex.new
    @user = nil
    @tags = {}
    @contexts = {}
    @breadcrumbs = []

    class << self
      # Isolate enrichment to one request. Anything set inside is visible only to events captured
      # inside. The Rack middleware wraps every request in this; call it directly around a background
      # job, which has the same problem of many in flight at once.
      def request
        previous = Thread.current[STATE_KEY]
        Thread.current[STATE_KEY] = { user: nil, tags: {}, contexts: {}, breadcrumbs: [] }
        yield
      ensure
        # Always restore. Puma reuses threads, so state left behind is handed to the next request that
        # worker picks up, which is the exact leak this exists to prevent. Restoring the previous value
        # rather than clearing keeps nesting honest.
        Thread.current[STATE_KEY] = previous
      end

      # Nil when no request is in flight, so writes fall through to process state and a boot-time
      # set_tag behaves exactly as it did before request scopes existed.
      def request_state
        Thread.current[STATE_KEY]
      end
      # Attach the signed-in user (id/email/username) to subsequent events; nil clears. Inside a request
      # scope this applies to that request alone; outside one it is process wide.
      def user=(user)
        value = user&.transform_keys(&:to_s)
        state = request_state
        return state[:user] = value if state

        @mutex.synchronize { @user = value }
      end

      # Attach a tag to subsequent events; a nil value removes it.
      def set_tag(key, value)
        state = request_state
        if state
          value.nil? ? state[:tags].delete(key.to_s) : state[:tags][key.to_s] = value
          return
        end

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
        value = context&.transform_keys(&:to_s)
        state = request_state
        if state
          value.nil? ? state[:contexts].delete(name.to_s) : state[:contexts][name.to_s] = value
          return
        end

        @mutex.synchronize do
          if value.nil?
            @contexts.delete(name.to_s)
          else
            @contexts[name.to_s] = value
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

        state = request_state
        if state
          state[:breadcrumbs] << crumb
          state[:breadcrumbs].shift while state[:breadcrumbs].length > MAX_BREADCRUMBS
          return
        end

        @mutex.synchronize do
          @breadcrumbs << crumb
          @breadcrumbs.shift while @breadcrumbs.length > MAX_BREADCRUMBS
        end
      end

      # Reset all ambient state (tests, or a full sign-out). Clears the request scope when one is active.
      def clear
        state = request_state
        if state
          state.replace(user: nil, tags: {}, contexts: {}, breadcrumbs: [])
          return
        end

        @mutex.synchronize do
          @user = nil
          @tags = {}
          @contexts = {}
          @breadcrumbs = []
        end
      end

      # The scope's contribution to an event, holding only the keys that are actually set so an
      # unenriched event keeps its exact wire shape. Breadcrumbs use the Sentry {"values" => []} envelope.
      #
      # The request scope layers OVER the process state rather than replacing it, so a request keeps the
      # deployment-wide tags while overriding the ones it sets itself.
      def fields
        state = request_state || {}
        process = @mutex.synchronize do
          { user: @user&.dup, tags: @tags.dup, contexts: @contexts.dup, breadcrumbs: @breadcrumbs.dup }
        end

        user = state[:user] || process[:user]
        tags = process[:tags].merge(state[:tags] || {})
        contexts = process[:contexts].merge(state[:contexts] || {})
        # Concatenated, not merged: the trail is a sequence, and the process-level crumbs genuinely
        # happened before the ones recorded during the request.
        breadcrumbs = (process[:breadcrumbs] + (state[:breadcrumbs] || [])).last(MAX_BREADCRUMBS)

        fields = {}
        fields["user"] = user.dup if user
        fields["tags"] = tags unless tags.empty?
        fields["contexts"] = contexts unless contexts.empty?
        fields["breadcrumbs"] = { "values" => breadcrumbs } unless breadcrumbs.empty?
        fields
      end
    end
  end
end
