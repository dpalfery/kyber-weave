---
schema: kyber-squad.migration/v1
agent: pulumi-dev
source-commit: d7547f46ab6bb8e447096345abbe5d4c7840bfc0
selected-baseline: .claude/agents/pulumi-dev.md
sources:
  .claude/agents/pulumi-dev.md: 28382557b95d98bb1fa7e9f6ee2e45098e65ff9ae7fdf33d85197e6e9c88b09b
  .codex/agents/pulumi-dev.toml: 86292bb21823ca958fdde3ed32f12064eda4c1baba0456ed91d0f897b84c4c72
  .cursor/agents/pulumi-dev.agent.md: 87c7ab97c90c3d048d5c0b1c98f2e22c10bb0f5bd8b669ac7e736c57cb792448
  .github/agents/pulumi-dev.agent.md: 8f05c11bbdf58801e749d4b5b07ff903fa7b281d4acf60fc4743154f9a8fdc8d
  .opencode/agents/pulumi-dev.md: 802635c4c85532f1a0a3c78198cef9212494e73b352e139fe98ac280db84aea8
final-body-sha256: dd678d36c794d5280dbd989d46688c7004c3abd90a77e336f5495233bad05556
---
# pulumi-dev migration

## Baseline and reconciliation

The canonical body starts from .claude/agents/pulumi-dev.md at the locked source commit. No alternate harness body is retained. Other live variants were compared for harness-independent behavior; none added behavior that could be merged without changing the selected role contract.

Source frontmatter, provider model identifiers, tool allowlists, and command-shaped invocation syntax are excluded from the instruction body. Equivalent intent is represented by canonical invocation, model, capability, delegation, fallback, and alias metadata.

## Permission resolution

The worker profile is the conservative intersection of effective live permissions after unsupported capabilities are marked unavailable: filesystem.read=allow, filesystem.write=allow, process.execute=allow, network.read=deny, network.publish=deny, delegate=deny. Scoped source grants resolve to ask when a broad allow would widen access; explicit denials remain deny.

The final digest is calculated from the UTF-8, LF-normalized body loaded from the canonical agent file.
