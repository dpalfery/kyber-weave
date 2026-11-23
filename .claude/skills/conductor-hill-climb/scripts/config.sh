# Shared settings for the conductor hill-climb scripts. Every value can be overridden
# from the environment, so nothing here is machine-specific once you set them.
# Source this file; do not run it.

HC_SCRIPTS="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"                      # absolute; scripts cd elsewhere
: "${HC_KW_REPO:=$(cd "$HC_SCRIPTS/../../../.." && pwd)}"                     # kyber-weave checkout
: "${HC_DEMO_REPO:=$HOME/git/personal/factory/hotshot-logistics-demo}"       # demo app, canonical checkout
: "${HC_DEMO_PROMPT:=$HC_DEMO_REPO/6-Docs/demo/Demo-full-prompt.md}"         # the feature prompt
: "${HC_HOME:=$(dirname "$HC_KW_REPO")/hill-climb}"                          # runs, ledger, local releases
: "${HC_CONDUCTOR_MODEL:=gpt-6.2-sol}"      # conductor.agent.md pins no model; the user default may be unavailable
: "${HC_RUN_TIMEOUT_MIN:=180}"              # a run past this is scored as a hang; healthy runs with three review cycles took over 2 h
: "${HC_AUTOPILOT_CONTINUES:=20}"           # Copilot's default of 5 can end a long run early
: "${HC_MAX_AI_CREDITS:=}"                  # optional per-run spend cap; empty means none
: "${HC_MAX_RUNS:=10}"

HC_RUNS="$HC_HOME/runs"
HC_LEDGER="$HC_HOME/ledger.md"
mkdir -p "$HC_RUNS"

hc_log() { printf '[hill-climb] %s\n' "$*" >&2; }
hc_die() { hc_log "error: $*"; exit 1; }

# Path of the newest run directory, or empty.
hc_latest_run() { ls -1d "$HC_RUNS"/run-* 2>/dev/null | sort | tail -n 1; }
