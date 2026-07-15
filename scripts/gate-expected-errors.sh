#!/usr/bin/env bash
# Gate: deliberate, asserted test failures must stay MARKED (#236, #255).
#
# A green test run legitimately logs some exceptions — a tool asked to open an unopenable database, a backend
# killed mid-stream, a hung backend hitting its timeout. Each is a negative test doing its job. #236 made those
# quiet-but-labelled: the test calls ExpectedError.Emit(...) so the alarming line arrives wearing an
# "[EXPECTED ERROR]" tag. The rule that buys us: anything loud in a green run NOT preceded by the marker is a
# REAL signal (this is exactly how the CS8604 warning hid on every run for months — #209).
#
# This turns that convention into a guard that can fail. It is COUNT-BASED on purpose, not adjacency-based:
# xUnit runs test classes in parallel, so a marker and the error it explains can be far apart in the combined
# log — but their COUNTS do not care about interleaving. Each deliberate error emits exactly one tagged header
# line and its test emits exactly one marker, so in a clean run (# error headers) == (# markers). An unmarked
# deliberate error (or a real, unexpected one) pushes headers above markers and fails the build.
#
# Usage: gate-expected-errors.sh <path-to-captured-test-log>
set -u

log="${1:?usage: gate-expected-errors.sh <test-log-file>}"
if [ ! -f "$log" ]; then
  echo "gate-expected-errors: log file not found: $log" >&2
  exit 2
fi

# Tagged, stack-trace-bearing error headers written by the production error paths (McpRequestHandler,
# EndpointHelpers.Fail and the endpoint catch blocks). One line per logged exception.
header_re='^\[(API|MCP[^]]*Error)\]'
marker='[EXPECTED ERROR]'

headers=$(grep -cE "$header_re" "$log" || true)
markers=$(grep -cF "$marker" "$log" || true)

echo "expected-error gate: ${headers} error header(s), ${markers} marker(s)"

if [ "$headers" -gt "$markers" ]; then
  echo ""
  echo "FAIL: ${headers} deliberate-error log line(s) but only ${markers} [EXPECTED ERROR] marker(s)." >&2
  echo "An error was logged in a green run without being marked. If it is a deliberate negative test, mark it" >&2
  echo "with ExpectedError.Emit(...) right before the action (see tests/Shonkor.Tests/ExpectedError.cs). If it" >&2
  echo "is NOT deliberate, you just found a real problem the marker convention is meant to surface." >&2
  echo "" >&2
  echo "Unmarked-looking error headers:" >&2
  grep -nE "$header_re" "$log" | sed 's/^/  /' >&2
  exit 1
fi

echo "expected-error gate: OK (every deliberate error is accounted for by a marker)."
