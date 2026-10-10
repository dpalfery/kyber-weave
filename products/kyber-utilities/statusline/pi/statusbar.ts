/**
 * Pi status-line extension — a standalone Kyber Utilities artifact.
 *
 * `activate(ctx)` renders one branded bar from the session fields pi exposes
 * on the extension context:
 *
 *   [PI] 📁 repo │ 🌿 branch │ 🤖 model │ ⚡ total (in:x out:y cache:z) │ 💰 $cost │ ⏳ turn #n
 *
 * THE THING TO GET RIGHT: there is no single output that works in both modes.
 * In "tui" mode pi owns the screen and repaints it, so the bar goes through
 * `ctx.ui.setStatus` plus a below-editor `ctx.ui.setWidget` — a raw write to
 * stderr would be torn up by the next repaint. In every other mode there is
 * no widget surface and nothing repaints, so a plain stderr write is both
 * safe and the only option.
 *
 * The extension opens no sockets, writes no files, and echoes no environment.
 * It depends on nothing but the context pi hands it.
 */

/** The session fields this bar needs, read straight off the pi context. */
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

/** The pinned slice of the pi extension context this artifact reads. */
interface StatusLineContext {
  ui?: {
    /** "tui" means a live terminal UI owns the screen — do not write to it. */
    mode?: string;
    setStatus?: (text: string) => void;
    /** Persistent bar rendered below the editor; undefined content removes it. */
    setWidget?: (text: string, options?: { placement?: string }) => void;
  };
  cwd?: string;
  model?: string | { id?: string; name?: string };
  branch?: string;
  tokens?: { input?: number; output?: number; cache?: number; total?: number };
  costUsd?: number;
  turns?: number;
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
 * pi clips a widget line at the terminal edge rather than wrapping it, so a bar
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

/** pi may hand the model as a plain string or as an {id, name} object. */
function modelLabel(model: StatusLineContext['model']): string | undefined {
  if (typeof model === 'string') return model || undefined;
  if (model && typeof model === 'object') {
    const value = model.name ?? model.id;
    return value === undefined || value === null ? undefined : String(value);
  }
  return undefined;
}

/** Fold the extension context into the summary the segments render from. */
function summaryFromContext(ctx: StatusLineContext): StatusLineSummary {
  return {
    repo: basename(ctx.cwd),
    branch: ctx.branch,
    model: modelLabel(ctx.model),
    freshInput: ctx.tokens?.input,
    output: ctx.tokens?.output,
    cacheRead: ctx.tokens?.cache,
    cacheCreation: 0,
    totalTokens: ctx.tokens?.total,
    totalCostUsd: ctx.costUsd,
    turnCount: ctx.turns,
  };
}

/**
 * Render the status bar by whichever route the current mode actually shows.
 *
 * In "tui" mode the bar must be ui-owned — `setStatus` for the compact line
 * plus `setWidget` for the full bar below the editor. Outside "tui"
 * (print/json/rpc) there is no such surface, so stderr is the only safe write.
 * Both calls are best-effort: a ui failure must never take the session down
 * with it.
 */
function renderToUi(ctx: StatusLineContext, summary: StatusLineSummary): void {
  if (ctx.ui?.mode === 'tui') {
    try {
      ctx.ui.setStatus?.(renderStatusBar(summary));
    } catch {
      // Swallow — the status line is best-effort.
    }
    // Fall back to 80 when the width is unknown (not a tty): too narrow is
    // recoverable -- it just wraps -- whereas guessing too wide clips.
    const columns = process.stdout.columns || 80;
    try {
      ctx.ui.setWidget?.(renderStatusBarLines(summary, columns).join('\n'),
        { placement: 'belowEditor' });
    } catch {
      // Swallow — the status line is best-effort.
    }
    return;
  }

  process.stderr.write(renderStatusBar(summary) + '\n');
}

/**
 * Extension entry point. pi hands us the context; the bar is painted once from
 * whatever the session already knows.
 */
export function activate(ctx: StatusLineContext): void {
  try {
    renderToUi(ctx, summaryFromContext(ctx));
  } catch {
    // Swallow — the status line is best-effort and must never fail the host.
  }
}

export default activate;
