#!/usr/bin/env bash
# Claude Code status line. Renders one full-width bar:
#   user@host | …/dir | git:branch [~2 +1 ?3] | model:X | output:Y | ctx:41%
#
# Width note: Claude Code captures stdout instead of attaching it to the
# terminal, so `tput cols` and language-level width detection cannot see the
# real size. Claude Code exports COLUMNS/LINES before running this script
# (v2.1.153+); that is the only reliable source. When COLUMNS is absent the bar
# is emitted unpadded rather than truncated to a guessed width, which would
# silently clip the trailing segments.

set -uo pipefail

input=$(cat)

IFS=$'\t' read -r cur_dir model_name output_style ctx_pct < <(
  printf '%s' "$input" | jq -r '
    [ (.workspace.current_dir // .cwd // ""),
      (.model.display_name // ""),
      (.output_style.name // ""),
      (.context_window as $c
       | (($c.used_percentage // null) as $p
          | if $p != null
            then (($p | if type == "number" then floor else . end) | tostring) + "%"
            elif ($c.context_window_size // 0) > 0
            then ((($c.current_usage.input_tokens // 0)
                 + ($c.current_usage.cache_creation_input_tokens // 0)
                 + ($c.current_usage.cache_read_input_tokens // 0))
                 * 100 / $c.context_window_size | floor | tostring) + "%"
            else "N/A" end))
    ] | @tsv'
)

# --- git ---------------------------------------------------------------
git_info=""
if [ -n "$cur_dir" ] && branch=$(git -C "$cur_dir" rev-parse --abbrev-ref HEAD 2>/dev/null); then
  porcelain=$(git -C "$cur_dir" status --porcelain 2>/dev/null)
  if [ -n "$porcelain" ]; then
    state=$(printf '%s\n' "$porcelain" | awk '
      /^\?\?/ { u++; next }
      /^.?M/  { m++ }
      /^.?A/  { a++ }
      /^.?D/  { d++ }
      END {
        s = ""
        if (m) s = s " ~" m
        if (a) s = s " +" a
        if (d) s = s " -" d
        if (u) s = s " ?" u
        sub(/^ /, "", s)
        print s
      }')
    [ -n "$state" ] && git_info=" | git:$branch [$state]" || git_info=" | git:$branch [clean]"
  else
    git_info=" | git:$branch [clean]"
  fi
fi

# --- shorten path: last two segments when there is a real parent ---------
# A two-component path ("/x/name") has only one meaningful segment to show;
# printing "…/x/name" would just restate the full path with a prettier prefix.
short_dir="$cur_dir"
case "$cur_dir" in
  */*/*/*) short_dir="…/$(basename "$(dirname "$cur_dir")")/$(basename "$cur_dir")" ;;
  */*/*)   short_dir="…/$(basename "$cur_dir")" ;;
esac

# --- assemble ----------------------------------------------------------
line="${USER:-$(id -un 2>/dev/null)}@$(hostname -s 2>/dev/null) | $short_dir$git_info"
[ -n "$model_name" ]   && line="$line | model:$model_name"
[ -n "$output_style" ] && line="$line | output:$output_style"
line="$line | ctx:$ctx_pct"

width=${COLUMNS:-0}

# Truncate only when the width is actually known; pad to the edge when it fits.
if [ "$width" -gt 0 ] 2>/dev/null; then
  if [ "${#line}" -gt "$width" ]; then
    line="${line:0:$width}"
  fi
  printf '\033[48;2;13;71;161m\033[97m%s%*s\033[0m\n' "$line" "$((width - ${#line}))" ''
else
  printf '\033[48;2;13;71;161m\033[97m%s\033[0m\n' "$line"
fi
