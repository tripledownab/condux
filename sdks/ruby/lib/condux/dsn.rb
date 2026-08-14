# frozen_string_literal: true

require "uri"

module Condux
  # A parsed DSN: the relay endpoint, the project id path segment, and the public key.
  class Dsn
    attr_reader :endpoint, :project_id, :public_key

    def self.parse(dsn)
      uri = URI(dsn)
      project_id = uri.path.to_s.sub(%r{\A/}, "")
      if uri.user.nil? || uri.user.empty? || uri.host.nil? || project_id.empty?
        raise ArgumentError, "Condux: DSN must be scheme://<key>@<host>/<projectId>"
      end

      endpoint = "#{uri.scheme}://#{uri.host}"
      endpoint += ":#{uri.port}" if uri.port && ![80, 443].include?(uri.port)
      new(endpoint, project_id, uri.user)
    end

    def initialize(endpoint, project_id, public_key)
      @endpoint = endpoint
      @project_id = project_id
      @public_key = public_key
    end

    # The Sentry store endpoint an event is POSTed to.
    def store_url
      "#{endpoint}/api/#{project_id}/store/"
    end
  end
end
