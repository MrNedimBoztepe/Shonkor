#!/usr/bin/env bash
# rg-only-hook.test.sh — replays rg-command-cases.tsv through the REAL hook (#513, #514).
#
# The other half of the same table is Ap6RunReaderTests.IsRgCommand_MatchesTheSharedCaseTable. That is the
# whole point: the hook decides what the rg arm may execute and IsRgCommand decides what the run is judged
# as, so a rule that changes on one side only must fail a test, not survive as a promise in a comment.
#
# Run it directly (bash bench/golden/ap6/rg-only-hook.test.sh) or let the test suite run it
# (Ap6RgOnlyHookTests). Exit 0 = every case matched, 1 = at least one did not (each is printed).
set -u
export LC_ALL=C
# Git Bash/MSYS2 rewrites POSIX-looking arguments and environment values when it starts a native Windows
# process, which would turn the case "/usr/bin/rg Foo" into "C:/Program Files/Git/usr/bin/rg Foo" on the way
# into node. The hook itself is unaffected (its payload arrives on stdin, which is never converted); this is
# only about building the payload faithfully on Windows.
export MSYS2_ARG_CONV_EXCL='*' MSYS2_ENV_CONV_EXCL='*' MSYS_NO_PATHCONV=1

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOOK="$HERE/rg-only-hook.sh"
CASES="$HERE/rg-command-cases.tsv"

[ -f "$HOOK" ] || { echo "FAIL: no rg-only-hook.sh next to this script"; exit 1; }
[ -f "$CASES" ] || { echo "FAIL: no rg-command-cases.tsv next to this script"; exit 1; }
command -v node >/dev/null 2>&1 || { echo "FAIL: node is not on PATH — the hook needs it to read its payload"; exit 1; }

pass=0; fail=0

# The hook's verdict for a raw payload: 0 = allowed, 2 = blocked. Anything else is a broken hook.
hook_verdict() {
  printf '%s' "$1" | bash "$HOOK" >/dev/null 2>&1
  echo "$?"
}

# A JSON payload carrying $1 as tool_input.command, built by node so the escaping is JSON's own.
payload_for() {
  COMMAND_TEXT="$1" node -e 'process.stdout.write(JSON.stringify({ hook_event_name: "PreToolUse", tool_name: "Bash", tool_input: { command: process.env.COMMAND_TEXT } }))'
}

check() { # <what> <expected exit> <actual exit>
  if [ "$2" = "$3" ]; then pass=$((pass + 1)); else fail=$((fail + 1)); echo "FAIL: $1 — expected exit $2, got $3"; fi
}

# ---------- the shared table ----------
while IFS= read -r line || [ -n "$line" ]; do
  line="${line%$'\r'}"
  case "$line" in ''|'#'*) continue ;; esac
  raw_cmd="${line%%$'\t'*}"
  verdict="${line#*$'\t'}"
  verdict="${verdict%%$'\t'*}"
  # Only \\ \t \n \r \v \f \uXXXX occur in the table; %b decodes exactly those (the C# side throws on
  # anything else, which is what keeps the two decoders on one alphabet).
  cmd="$(printf '%b' "$raw_cmd")"
  case "$verdict" in
    allow) expected=0 ;;
    deny)  expected=2 ;;
    *) fail=$((fail + 1)); echo "FAIL: unreadable verdict '$verdict' for case [$raw_cmd]"; continue ;;
  esac
  check "[$raw_cmd]" "$expected" "$(hook_verdict "$(payload_for "$cmd")")"
done < "$CASES"

# ---------- payload-level cases, which have no command text to put in the table ----------
check "payload that is not JSON"        2 "$(hook_verdict 'not json at all')"
check "payload without a command field" 2 "$(hook_verdict '{"tool_name":"Bash","tool_input":{}}')"
check "empty payload"                   2 "$(hook_verdict '')"
# $(...) strips trailing newlines; the hook must not, or a command IsRgCommand rejects would run.
check "command with a trailing newline" 2 "$(hook_verdict "$(payload_for 'rg -n Foo src
')")"

echo "rg-only-hook: $pass passed, $fail failed"
[ "$fail" -eq 0 ]
