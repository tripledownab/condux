# frozen_string_literal: true

module Condux
  # Event severity, matching the levels the relay understands. The wire value is the lowercase string.
  module Level
    DEBUG = "debug"
    INFO = "info"
    WARNING = "warning"
    ERROR = "error"
    FATAL = "fatal"
  end
end
