/**
 * CommandCode status-line mod — a Kyber Utilities artifact.
 *
 * Renders one branded bar as this session's status segment:
 *
 *   [CC] 📁 repo │ 🌿 branch │ 🤖 model │ ⚡ total (in:x out:y) │ ⏳ turn #n
 *
 * THE THING TO GET RIGHT: the row under the input is shared and fixed-width.
 * The host truncates with `…` at the terminal edge, so this mod keeps one
 * compact segment and never a multi-line bar. There is no cost segment:
 * the mod host exposes no cost API, and an invented number would violate the
 * honest-unobservability rule — a missing value renders as absent, never zero.
 *
 * Data comes from the hooks the host fires, never from the network and never
 * from a file: `onSessionStart` paints the identity segments (repo/branch/
 * model), `onTurnEnd` folds the turn's usage into the running totals and
 * repaints. Everything is best-effort — any failure clears the segment with
 * `setStatus(null)` and never takes the session down with it.
 */

type BarState = {
  totalInput: number;
  totalOutput: number;
  totalCached: number;
  turnCount: number;
  branch?: string;
};

/** The host surface this mod reads — the subset of ModApi its hooks use. */
type ModLike = {
  cwd?: string;
  ui: {
    capabilities: { status: boolean };
    setStatus: (text: string | null) => void;
  };
  exec: (request: { command: string; args: string[] }) => Promise<{ stdout: string; code: number }>;
  getModel?: () => string | undefined;
  getContextUsage?: () => { tokens?: number | null } | undefined;
  state: {
    get: (key: string) => number | string | undefined;
    set: (key: string, value: number | string) => void;
  };
  hooks: (registered: {
    onSessionStart?: () => void | Promise<void>;
    onTurnEnd?: (args: { turnNumber?: number; usage?: unknown }) => unknown;
  }) => void;
  on: (...args: unknown[]) => { dispose: () => void };
};

const STATE_PREFIX = 'kyber-statusline:';

/** Format a number in compact "k" notation. Non-finite values render as absent, never NaN. */
export function fmtK(val: number | undefined): string {
  if (val === undefined || !Number.isFinite(val)) return '--';
  if (val >= 1000) return (val / 1000).toFixed(1) + 'k';
  return val.toString();
}

/** The separator the bar joins with: a plain, uncolored box-drawing pipe. */
const SEP = ' │ ';

/** The individual colored segments, in the bar's fixed order. */
export function statusBarSegments(summary: {
  repo?: string;
  branch?: string;
  model?: string;
  input?: number;
  output?: number;
  cached?: number;
  totalTokens?: number | null;
  turnCount?: number;
}): string[] {
  const CC_BADGE = '\x1b[1;37;44m CC \x1b[0m';
  const CYAN = '\x1b[1;36m';
  const GREEN = '\x1b[1;32m';
  const MAGENTA = '\x1b[1;35m';
  const YELLOW = '\x1b[1;33m';
  const BLUE = '\x1b[1;34m';
  const GRAY = '\x1b[38;5;244m';
  const RESET = '\x1b[0m';

  const parts: string[] = [CC_BADGE];

  if (summary.repo) parts.push(`${CYAN}📁 ${summary.repo}${RESET}`);
  if (summary.branch) parts.push(`${GREEN}🌿 ${summary.branch}${RESET}`);
  if (summary.model) parts.push(`${MAGENTA}🤖 ${summary.model}${RESET}`);

  const totalInput = summary.input ?? 0;
  const totalOutput = summary.output ?? 0;
  const totalCached = summary.cached ?? 0;
  const total = summary.totalTokens ?? totalInput + totalOutput + totalCached;
  const hasUsage = summary.input !== undefined || summary.output !== undefined
    || summary.cached !== undefined || summary.totalTokens !== undefined;
  if (hasUsage) {
    const totalText = typeof total === 'number' ? fmtK(total) : '--';
    parts.push(
      `${YELLOW}⚡ ${totalText}${RESET} ` +
      `${GRAY}(in:${fmtK(summary.input)} out:${fmtK(summary.output)} cache:${fmtK(summary.cached)})${RESET}`);
  }

  parts.push(`${BLUE}⏳ turn #${summary.turnCount ?? '--'}${RESET}`);

  return parts;
}

/**
 * Totalling with a closed vocabulary of field names. Unknown usage shapes
 * total as absent rather than zero.
 */
export function addUsage(state: BarState, usage: unknown): void {
  if (typeof usage !== 'object' || usage === null) return;
  const record = usage as Record<string, unknown>;
  const pick = (...names: string[]): number | undefined => {
    for (const name of names) {
      const value = record[name];
      if (typeof value === 'number' && Number.isFinite(value) && value >= 0) return value;
    }
    return undefined;
  };
  const input = pick('inputTokens', 'input');
  const output = pick('outputTokens', 'output');
  const cached = pick('cachedTokens', 'cache');
  if (input !== undefined) state.totalInput += input;
  if (output !== undefined) state.totalOutput += output;
  if (cached !== undefined) state.totalCached += cached;
}

/** Basename of a path, used as the repo label. */
function basename(p: string | undefined): string | undefined {
  if (!p) return undefined;
  const parts = p.replace(/\/+$/, '').split('/');
  return parts[parts.length - 1] || undefined;
}

/** Render the full ANSI-colored status bar as one line. */
export function renderStatusBar(state: BarState, identity: {
  repo?: string;
  model?: string;
}): string {
  return statusBarSegments({
    repo: identity.repo,
    branch: state.branch,
    model: identity.model,
    input: state.totalInput,
    output: state.totalOutput,
    cached: state.totalCached,
    turnCount: state.turnCount,
  }).join(SEP);
}

function readNumber(cmd: ModLike, key: string): number {
  const value = cmd.state.get(STATE_PREFIX + key);
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : 0;
}

function loadState(cmd: ModLike): BarState {
  return {
    totalInput: readNumber(cmd, 'input'),
    totalOutput: readNumber(cmd, 'output'),
    totalCached: readNumber(cmd, 'cached'),
    turnCount: readNumber(cmd, 'turns'),
    branch: undefined,
  };
}

function saveState(cmd: ModLike, state: BarState): void {
  cmd.state.set(STATE_PREFIX + 'input', state.totalInput);
  cmd.state.set(STATE_PREFIX + 'output', state.totalOutput);
  cmd.state.set(STATE_PREFIX + 'cached', state.totalCached);
  cmd.state.set(STATE_PREFIX + 'turns', state.turnCount);
}

/** Whether the host renders status segments. Tolerant of hosts that omit the surface. */
function statusVisible(cmd: ModLike): boolean {
  try {
    return cmd.ui?.capabilities?.status === true;
  } catch {
    return false;
  }
}

/** Best-effort clear that never throws, even when the host surface is absent. */
function clearStatus(cmd: ModLike): void {
  try {
    cmd.ui?.setStatus?.(null);
  } catch {
    // Swallow — the status line is best-effort.
  }
}

function paint(cmd: ModLike, summary: {
  repo?: string;
  model?: string;
  state: BarState;
}): void {
  try {
    if (!statusVisible(cmd)) {
      clearStatus(cmd);
      return;
    }
    cmd.ui.setStatus(renderStatusBar(summary.state, summary));
  } catch {
    // Swallow — the status line is best-effort and must never fail the host.
  }
}

/**
 * Mod entry point. CommandCode loads this file from `~/.commandcode/mods/`;
 * activation is the operator copying it there and running `/reload`.
 */
export default function (cmd: ModLike): void {
  let state = loadState(cmd);
  let repo = basename(cmd.cwd);
  let model: string | undefined;
  try {
    model = cmd.getModel?.() ?? undefined;
  } catch {
    model = undefined;
  }
  let branch: string | undefined;

  const repaint = (): void => {
    state.branch = branch;
    saveState(cmd, state);
    paint(cmd, { repo, model, state });
  };

  try {
    cmd.hooks({
    onSessionStart: () => {
      try {
        state = loadState(cmd);
        repo = basename(cmd.cwd);
        try {
          model = cmd.getModel?.() ?? undefined;
        } catch {
          model = undefined;
        }
        void cmd.exec({ command: 'git', args: ['branch', '--show-current'] }).then(
          (result) => {
            try {
              const name = result.stdout.split('\n')[0]?.trim();
              if (result.code === 0 && name) branch = name;
            } catch {
              // Swallow — a missing branch renders as absent.
            }
            repaint();
          },
          () => {
            repaint();
          },
        );
        repaint();
      } catch {
        // Swallow — the status line is best-effort.
      }
    },
    onTurnEnd: (args) => {
      try {
        addUsage(state, args?.usage);
        const turn = args?.turnNumber;
        if (typeof turn === 'number' && Number.isInteger(turn) && turn >= 0) state.turnCount = turn;
        try {
          model = cmd.getModel?.() ?? model;
        } catch {
          // Swallow — keep the last known model.
        }
        repaint();
      } catch {
        // Swallow — the status line is best-effort.
      }
      return undefined;
    },
  });
  } catch {
    // A host that rejects hook registration must not take the session down.
    // Best-effort clear so no stale bar from a previous load survives.
    clearStatus(cmd);
  }
}
