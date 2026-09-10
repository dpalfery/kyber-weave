## How to build with CodeBurn Desktop (KyberDash UI)

This is the UI of a **local-first desktop app that shows where AI-coding spend goes** — dense,
data-heavy, calm. Screens are panels of numbers, not marketing pages. Content should look like
real telemetry: project names, model ids (`claude-opus-5`), session counts, USD costs, token
counts, date ranges.

### Setup — no provider, but two rules

Components need **no provider or context wrapper**. Just render them; `styles.css` carries
everything.

1. **Theme is an attribute on `<html>`**, not a class or a prop:
   `document.documentElement.setAttribute('data-theme', 'dark')` — or `'light'`, or remove the
   attribute to use the default. The default (no attribute) is the light surface. Only
   `data-theme` switches the palette; there is no `.dark` class in this system.
2. **`Window` is the app shell.** For a full screen, put everything inside it — it renders the
   `.win` frame that hosts a `Sidebar` rail plus a `.ct` content column. For a fragment or a
   single card, skip it.

### The styling idiom: semantic CSS classes + `var(--*)` tokens

This is **not** a utility-class system and **not** a style-props system. Components take real
props (`title`, `value`, `daily`, `error`) and emit their own semantic class names. Your own
layout glue uses the same vocabulary — never invent class names, and never reach for Tailwind
utilities, they do not exist here.

**Layout classes you compose with:**

| Family | Classes | Use |
|---|---|---|
| Card | `.panel`, `.phead`, `.pbody` | The card, its title strip, its content well (`Panel` renders these) |
| Rows | `.li`, `.li-clickable`, `.lx`, `.no`, `.mdot`, `.val`, `.chev` | List rows (`ListRow` renders these) |
| Metric | `.stat`, `.v`, `.d` | Metric card, big value, delta line (`Stat`); `.stats` is the grid that holds them |
| Chrome | `.win`, `.ct`, `.bar`, `.sb`, `.ni`, `.foot` | Window frame, content column, top bar, sidebar rail, nav item, footer strip |
| Controls | `.seg`, `.pop`, `.pop-menu`, `.pop-item` | Segmented control, popover trigger/menu/item |
| Text | `.empty-note`, `.hint`, `.k`, `.r` | Empty state, footer hint strip, keycap, right-aligned slot |
| Utility | `.scroll-x` | Horizontal scroll container for wide tables/charts |

**Tokens** — use these instead of literal values:

- Spacing: `--sp-1` … `--sp-7` (4/8/12/16/20/24/32px)
- Type size: `--fs-hero`, `--fs-stat`, `--fs-kpi`, `--fs-subhead`, `--fs-body`, `--fs-meta`,
  `--fs-label`, `--fs-micro`
- Type weight: `--fw-body`, `--fw-medium`, `--fw-subhead`, `--fw-strong`, `--fw-kpi`, `--fw-hero`
- Surface: `--canvas`, `--bg`, `--panel`, `--phead`, `--side`, `--fill`, `--hover`
- Line/ink: `--line`, `--line2`, `--ink`, `--mut`, `--mut2`
- Accent/state: `--accent`, `--accent-text`, `--ok`, `--warn`, `--bad`
- Families: `--sans`, `--mono`
- Model series (keep model colours consistent across charts): `--s-opus`, `--s-sonnet`,
  `--s-haiku`, `--s-fable`, `--s-gpt`, `--s-other`

**Numbers are always mono and tabular.** Any cost, count or token figure gets
`font-family: var(--mono); font-variant-numeric: tabular-nums;` — that is the strongest single
signal of this design system. Costs read as `$184.20`, counts as `1,204,880`.

### Where the truth lives

Read these before styling anything — they beat this summary:

- `_ds/<folder>/styles.css` and the files it `@import`s — the complete class and token source.
- `components/<Name>/<Name>.prompt.md` — per-component usage.
- `components/<Name>/<Name>.d.ts` — the prop contract. Note that several props are legitimately
  nullable (`customRange`, `configSource`, `value` on `RangeCalendar`); `null` is the normal
  case, not an error.

### One idiomatic composition

```jsx
<div style={{ display: 'grid', gap: 'var(--sp-3)' }}>
  <div className="stats">
    <Stat label="Total spend" value="$184.20" delta="+12% vs last week" />
    <Stat label="Sessions" value="412" delta="34 today" />
  </div>

  <Panel title="Spend by project" right="See all ›" rightLink>
    <ListRow no="01" title="kyber-weave" sub="412 sessions" value="$184.20" onClick={() => {}} />
    <ListRow no="02" title="codeburn"    sub="288 sessions" value="$121.75" onClick={() => {}} />
  </Panel>

  <Panel title="Pull requests" right="Last 30 days">
    <EmptyNote>No pull requests in this range.</EmptyNote>
  </Panel>
</div>
```

Note what the glue does and does not do: the outer grid uses a `--sp-*` token, the metric row
uses the system's own `.stats` class, and everything visual comes from the components. That is
the house style — reach for a component first, a system class second, and a raw value never.
