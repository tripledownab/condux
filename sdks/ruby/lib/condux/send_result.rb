# frozen_string_literal: true

module Condux
  # Outcome of a delivery attempt sequence. Never raised — inspect #ok.
  SendResult = Struct.new(:ok, :attempts, :status, :error, keyword_init: true)
end
