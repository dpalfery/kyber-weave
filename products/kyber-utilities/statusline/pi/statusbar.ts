/**
 * Pi status-line extension — a standalone Kyber Utilities artifact.
 *
 * `export default` receives the pi extension host and registers a custom footer
 * for interactive sessions:
 *
 *   [PI] 📁 repo │ 🌿 branch │ 🤖 model │ ⚡ total (in:x out:y cache:z) │ 💰 $cost │ ⏳ turn #n
 *
 * THE THING TO GET RIGHT: the bar must be ui-owned. In "tui" mode pi owns the
 * screen and repaints it, so the bar is a `ctx.ui.setFooter()` component that pi
 * renders every frame — a raw write to stderr would be torn up by the next
 * repaint. Outside "tui" (print/json/rpc, including `--help`) there is no
 * footer surface, so the extension registers nothing and writes nothing; a
 * load-time stderr write would pollute command output.
 *
 * Live data comes from three places, matching the pi 1.3 extension host:
 *
 * - repository: the basename of `ctx.cwd`;
 * - branch: `footerData.getGitBranch()`, re-rendering on `onBranchChange`;
 * - model, tokens, cost, turns: `ctx.model` plus the assistant usage summed
 *   across `ctx.sessionManager.getBranch()`, re-rendered on `agent_end` and
 *   `agent_settled`.
 *
 * The extension opens no sockets, writes no files, and echoes no environment.
 * It depends on nothing but the host pi hands it and imports no packages, so
 * the staged file also loads under plain `node --test` harnesses.
 */

/** The session fields this bar needs, folded from the pi session. */
interface StatusLineSummary {
  repo?: string;
  branch?: string;
  model?: string;
  freshInput?: number;
  output?: number;
  cacheRead?: number;
  cacheCreation?: number;
  totalTokens?: number;
  totalCostUsd?: number;
  turnCount?: number;
}

/** The per-message usage pi reports on assistant session entries. */
interface AssistantUsage {
  input?: number;
  output?: number;
  cacheRead?: number;
  cacheWrite?: number;
  totalTokens?: number;
  cost?: { total?: number };
}

/** The slice of a pi session entry this artifact reads. */
interface SessionBranchEntry {
  type?: string;
  message?: {
    role?: string;
    usage?: AssistantUsage;
  };
}

/** The slice of pi's footer-data provider this artifact reads. */
interface FooterDataView {
  getGitBranch(): string | null;
  onBranchChange(callback: () => void): () => void;
}

/** The slice of pi's TUI handle this artifact reads. */
interface TuiView {
  requestRender(): void;
}

/** The footer component shape pi renders below the editor. */
interface FooterComponent {
  render(width: number): string[];
  invalidate(): void;
  dispose?(): void;
}

/** The slice of the pi session context this artifact reads. */
interface SessionContextView {
  mode?: string;
  cwd?: string;
  model?: { id?: string; name?: string };
  sessionManager?: { getBranch(): SessionBranchEntry[] };
  ui?: {
    setFooter(
      factory:
        | ((tui: TuiView, theme: unknown, footerData: FooterDataView) => FooterComponent)
        | undefined,
    ): void;
  };
}

/** The slice of the pi extension host this artifact registers with. */
interface ExtensionHostView {
  on(event: string, handler: (...args: never[]) => void): () => void;
}

/** Format a number in compact "k" notation. */
export function fmtK(val: number | undefined): string {
  if (val === undefined) return '--';
  if (val >= 1000) return (val / 1000).toFixed(1) + 'k';
  return val.toString();
}

/** Format a USD value for display. */
export function fmtUsd(val: number | undefined): string {
  if (val === undefined) return '--';
  if (val >= 1.0) return '$' + val.toFixed(2);
  if (val >= 0.01) return '$' + val.toFixed(4);
  return '$' + val.toFixed(4);
}

/** The separator the bar joins with: a plain, uncolored box-drawing pipe. */
const SEP = ' │ ';

/** Strip SGR escapes so a string can be measured as the terminal sees it. */
// eslint-disable-next-line no-control-regex
const ANSI = /\x1b\[[0-9;]*m/g;

/**
 * Approximate rendered column count.
 *
 * Emoji outside the BMP occupy two columns in every terminal that matters here,
 * and JS string length counts them as two UTF-16 units already -- so counting
 * code points and adding one back per astral character lands on the right
 * number without pulling in a full wcwidth table.
 */
export function visibleWidth(s: string): number {
  const plain = s.replace(ANSI, '');
  let w = 0;
  for (const ch of plain) w += (ch.codePointAt(0) ?? 0) > 0xffff ? 2 : 1;
  return w;
}

/**
 * Pack segments into lines that each fit `columns`.
 *
 * pi clips a footer line at the terminal edge rather than wrapping it, so a bar
 * wider than the window silently loses its tail -- on an 80-column terminal
 * that is the cost and turn segments, the two a person is most likely watching.
 * Wrapping keeps every segment on screen; a terminal too narrow for even one
 * segment still gets that segment, since dropping it would be worse.
 */
export function packSegments(segments: string[], columns: number): string[] {
  const sepWidth = visibleWidth(SEP);
  const lines: string[] = [];
  let line = '';
  let width = 0;

  for (const seg of segments) {
    const segWidth = visibleWidth(seg);
    if (line === '') {
      line = seg;
      width = segWidth;
      continue;
    }
    if (width + sepWidth + segWidth <= columns) {
      line += SEP + seg;
      width += sepWidth + segWidth;
    } else {
      lines.push(line);
      line = seg;
      width = segWidth;
    }
  }
  if (line !== '') lines.push(line);
  return lines;
}

/** The individual colored segments, in the bar's fixed order. */
export function statusBarSegments(summary: StatusLineSummary): string[] {
  const PI_BADGE = '\x1b[1;37;44m PI \x1b[0m';
  const CYAN = '\x1b[1;36m';
  const GREEN = '\x1b[1;32m';
  const MAGENTA = '\x1b[1;35m';
  const YELLOW = '\x1b[1;33m';
  const BLUE = '\x1b[1;34m';
  const GRAY = '\x1b[38;5;244m';
  const RESET = '\x1b[0m';

  const parts: string[] = [PI_BADGE];

  if (summary.repo) parts.push(`${CYAN}📁 ${summary.repo}${RESET}`);
  if (summary.branch) parts.push(`${GREEN}🌿 ${summary.branch}${RESET}`);
  if (summary.model) parts.push(`${MAGENTA}🤖 ${summary.model}${RESET}`);

  const totalTokens = summary.totalTokens
    ?? (summary.freshInput ?? 0) + (summary.cacheRead ?? 0)
      + (summary.cacheCreation ?? 0) + (summary.output ?? 0);
  const tokenBreak = `${GRAY}(in:${fmtK(summary.freshInput)} out:${fmtK(summary.output)} cache:${fmtK((summary.cacheRead ?? 0) + (summary.cacheCreation ?? 0))})${RESET}`;
  parts.push(`${YELLOW}⚡ ${fmtK(totalTokens)}${RESET} ${tokenBreak}`);

  parts.push(`${GREEN}💰 ${fmtUsd(summary.totalCostUsd)}${RESET}`);
  parts.push(`${BLUE}⏳ turn #${summary.turnCount ?? '--'}${RESET}`);

  return parts;
}

/**
 * Render the full ANSI-colored status bar as one line, joined with the plain,
 * uncolored separator so the segments line up visually.
 */
export function renderStatusBar(summary: StatusLineSummary): string {
  return statusBarSegments(summary).join(SEP);
}

/** The bar wrapped to `columns`, for surfaces that clip instead of wrapping. */
export function renderStatusBarLines(summary: StatusLineSummary, columns: number): string[] {
  return packSegments(statusBarSegments(summary), columns);
}

/** Basename of a path, used as the repo label. */
function basename(p: string | undefined): string | undefined {
  if (!p) return undefined;
  const parts = p.replace(/\/+$/, '').split('/');
  return parts[parts.length - 1] || undefined;
}

/** pi hands the model as an object with an id; fall back to a name. */
function modelLabel(model: SessionContextView['model']): string | undefined {
  if (!model) return undefined;
  const value = model.id ?? model.name;
  return value === undefined || value === null ? undefined : String(value);
}

/**
 * Fold a live pi session into the summary the segments render from.
 *
 * Token, cost, and turn counts come from the assistant entries on the current
 * branch, the same source pi's own footer reads; the branch arrives from the
 * footer-data provider, which watches the worktree for changes.
 */
export function summaryFromSession(
  cwd: string | undefined,
  modelId: string | undefined,
  branch: string | null | undefined,
  entries: SessionBranchEntry[] | undefined,
): StatusLineSummary {
  let freshInput = 0;
  let output = 0;
  let cache = 0;
  let cost = 0;
  let turns = 0;
  for (const entry of entries ?? []) {
    if (entry?.type !== 'message' || entry.message?.role !== 'assistant') continue;
    turns += 1;
    const usage = entry.message.usage;
    if (!usage) continue;
    freshInput += usage.input ?? 0;
    output += usage.output ?? 0;
    cache += (usage.cacheRead ?? 0) + (usage.cacheWrite ?? 0);
    cost += usage.cost?.total ?? 0;
  }
  return {
    repo: basename(cwd),
    branch: branch ?? undefined,
    model: modelId ?? undefined,
    freshInput,
    output,
    cacheRead: cache,
    cacheCreation: 0,
    totalTokens: freshInput + output + cache,
    totalCostUsd: cost,
    turnCount: turns,
  };
}

/**
 * Build the footer component pi renders below the editor. The component reads
 * the session live on every render, so tokens, cost, and turns stay current
 * without re-registration; branch changes arrive through the provider's watcher.
 */
export function createFooter(ctx: SessionContextView, footerData: FooterDataView, tui: TuiView): FooterComponent {
  let unsubscribe: (() => void) | undefined;
  try {
    unsubscribe = footerData.onBranchChange(() => {
      try {
        tui.requestRender();
      } catch {
        // Swallow — the status line is best-effort.
      }
    });
  } catch {
    unsubscribe = undefined;
  }
  return {
    invalidate() {},
    dispose() {
      try {
        unsubscribe?.();
      } catch {
        // Swallow — cleanup must never fail the host.
      }
    },
    render(width: number): string[] {
      const columns = typeof width === 'number' && width > 0 ? width : 80;
      const summary = summaryFromSession(
        ctx.cwd,
        modelLabel(ctx.model),
        footerData.getGitBranch(),
        ctx.sessionManager?.getBranch(),
      );
      return renderStatusBarLines(summary, columns);
    },
  };
}

/**
 * Extension entry point. pi calls this once on load; the footer itself is
 * registered per session, only for interactive ("tui") sessions that have a
 * footer surface. Re-renders are requested when an agent run ends and when
 * the branch watcher fires, so the bar tracks the session without polling.
 */
export default function (pi: ExtensionHostView): void {
  let activeTui: TuiView | undefined;
  const requestRender = () => {
    try {
      activeTui?.requestRender();
    } catch {
      // Swallow — the status line is best-effort and must never fail the host.
    }
  };
  try {
    pi.on('agent_end', requestRender);
    pi.on('agent_settled', requestRender);
    pi.on('session_shutdown', () => {
      activeTui = undefined;
    });
    pi.on('session_start', (_event: unknown, ctx: SessionContextView) => {
      try {
        if (!ctx || ctx.mode !== 'tui') return;
        if (typeof ctx.ui?.setFooter !== 'function') return;
        ctx.ui.setFooter((tui: TuiView, _theme: unknown, footerData: FooterDataView) => {
          activeTui = tui;
          return createFooter(ctx, footerData, tui);
        });
      } catch {
        // Swallow — the status line is best-effort and must never fail the host.
      }
    });
  } catch {
    // Swallow — the status line is best-effort and must never fail the host.
  }
}
