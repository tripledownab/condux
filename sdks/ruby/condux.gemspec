# frozen_string_literal: true

Gem::Specification.new do |spec|
  spec.name = "condux"
  spec.version = "0.1.0"
  spec.summary = "Report errors to a Condux relay (Sentry store wire shape)."
  spec.description = "The Condux SDK for Ruby: report errors to a Condux relay with a resilient, " \
                     "never-raising transport. Zero runtime dependencies (standard library only)."
  spec.authors = ["Condux"]
  spec.license = "Apache-2.0"
  spec.homepage = "https://condux.ai"
  spec.files = Dir["lib/**/*.rb", "README.md", "LICENSE"]
  spec.require_paths = ["lib"]
  spec.required_ruby_version = ">= 2.6"
  spec.metadata = {
    "homepage_uri" => "https://condux.ai",
    "source_code_uri" => "https://github.com/tripledownab/condux",
    "documentation_uri" => "https://github.com/tripledownab/condux/tree/main/sdks/ruby#readme",
    "rubygems_mfa_required" => "true"
  }
end
