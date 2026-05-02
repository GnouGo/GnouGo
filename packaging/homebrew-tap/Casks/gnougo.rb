# Template for GnouGo/homebrew-tap. Replace version and sha256 values from the GitHub release before publishing.
cask "gnougo" do
  version "0.0.0"
  arch arm: "arm64", intel: "x64"

  sha256 arm:   "0000000000000000000000000000000000000000000000000000000000000000",
         intel: "0000000000000000000000000000000000000000000000000000000000000000"

  url "https://github.com/GnouGo/GnouGo/releases/download/v#{version}/gnougo-osx-#{arch}.tar.gz"
  name "gnougo"
  desc "Friendly Bear Agent desktop application"
  homepage "https://github.com/GnouGo/GnouGo"

  app "gnougo.app"
end
