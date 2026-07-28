class KyberWeave < Formula
  desc "Governance CLI and MCP server for skills, agents, and documentation"
  homepage "https://github.com/dpalfery/kyber-weave"
  version "0.1.0"
  license "MIT"

  on_macos do
    on_arm do
      url "https://github.com/dpalfery/kyber-weave/releases/download/v#{version}/kyber-weave-osx-arm64.tar.gz"
      sha256 "REPLACE_WITH_RELEASE_SHA256_OSX_ARM64_CLI"

      resource "mcp" do
        url "https://github.com/dpalfery/kyber-weave/releases/download/v#{version}/kyber-weave-mcp-osx-arm64.tar.gz"
        sha256 "REPLACE_WITH_RELEASE_SHA256_OSX_ARM64_MCP"
      end
    end
    on_intel do
      url "https://github.com/dpalfery/kyber-weave/releases/download/v#{version}/kyber-weave-osx-x64.tar.gz"
      sha256 "REPLACE_WITH_RELEASE_SHA256_OSX_X64_CLI"

      resource "mcp" do
        url "https://github.com/dpalfery/kyber-weave/releases/download/v#{version}/kyber-weave-mcp-osx-x64.tar.gz"
        sha256 "REPLACE_WITH_RELEASE_SHA256_OSX_X64_MCP"
      end
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/dpalfery/kyber-weave/releases/download/v#{version}/kyber-weave-linux-arm64.tar.gz"
      sha256 "REPLACE_WITH_RELEASE_SHA256_LINUX_ARM64_CLI"

      resource "mcp" do
        url "https://github.com/dpalfery/kyber-weave/releases/download/v#{version}/kyber-weave-mcp-linux-arm64.tar.gz"
        sha256 "REPLACE_WITH_RELEASE_SHA256_LINUX_ARM64_MCP"
      end
    end
    on_intel do
      url "https://github.com/dpalfery/kyber-weave/releases/download/v#{version}/kyber-weave-linux-x64.tar.gz"
      sha256 "REPLACE_WITH_RELEASE_SHA256_LINUX_X64_CLI"

      resource "mcp" do
        url "https://github.com/dpalfery/kyber-weave/releases/download/v#{version}/kyber-weave-mcp-linux-x64.tar.gz"
        sha256 "REPLACE_WITH_RELEASE_SHA256_LINUX_X64_MCP"
      end
    end
  end

  def install
    bin.install "kyber-weave"
    resource("mcp").stage do
      bin.install "kyber-weave-mcp"
    end
  end

  test do
    assert_match "Kyber-Weave", shell_output("#{bin}/kyber-weave --help")
  end
end
