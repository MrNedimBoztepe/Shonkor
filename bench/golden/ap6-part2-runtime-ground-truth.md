# AP6 part 2 — the graph's `CALLS` edges against a real run

Executed under `ap6-precision-evidence-policy.md`. Read that first: it fixes what these numbers may
and may not be read as, and it was written before any of them existed.

## What was actually run

`dotnet-trace collect --format Speedscope -- shonkor.exe index C:\Projects\Brain` — the traced process
**is** the indexer, and the graph it produced is the graph it is compared against. Binary and graph
therefore cannot be on different revisions, which was the first thing that went wrong on the previous
attempt (see *Two attempts that produced nothing*).

| | |
|---|---|
| graph | `probe.db`, built by the traced run itself |
| `indexedRevision` | `b177da638b8d004326060c3e8df3bfb5ace8cf25` (= HEAD, clean tree) |
| `toolchainFingerprint` | `c631cb23cb5b33391c8299432118543fd8d259312dad4c137a32a965b3829057` |
| edges | 8 486, of which **0 unattributed** (`Reason = 0`) — #428 holds at HEAD |
| reasons | 5 129 SemanticSymbol · 3 146 Structural · 154 SyntacticHeritage · 26 UniqueNameMatch · 26 DocumentLink · 5 AmbiguousNameMatch |
| plugins | none installed — pure Core/Infrastructure parsers |
| trace | 580 456 open/close events across 40 threads, 4 526 frames → 3 284 distinct `Type::Method`, 6 518 adjacent pairs |

Name mapping between the two vocabularies (Speedscope `Module!Ns.Type.Method(args)` ↔ node id
`path::Ns.Type::Method#arity@offset`) reduces both to `Type::Method`, folding async state machines
(`Type+<M>d__7.MoveNext`), lambdas (`Type+<>c.<M>b__3_0`) and local functions back onto the declaring
method. The mapping is near-lossless on this graph: 2 138 edges → 2 132 distinct pairs, 15 self-pairs.

## Result

All 2 138 `CALLS` edges carry `Provenance = Extracted`, `Reason = SemanticSymbol`.

| reason | tier | confirmed | both ran, not adjacent | callee never ran | caller never ran |
|---|---|---|---|---|---|
| SemanticSymbol | Extracted | **60** | 35 | 68 | 1 975 |

- **confirmed** — the edge appears as an adjacent caller→callee pair in the reconstructed call tree.
- **both ran, not adjacent** — both methods were sampled but never as parent/child. A Release JIT
  inlines, so this is *not* separable from "wrong edge" with this instrument. It is not a refutation.
- **callee never ran / caller never ran** — the run says nothing about the edge.

**Nothing was refuted.** The word "contradicted" does not appear in this table on purpose: with a
sampling profiler and an inlining JIT, the absence of an adjacency carries no refuting force. 60 of 163
edges whose caller actually executed were confirmed; the other 103 are unresolved, not wrong.

Coverage is 163 / 2 138 = **7,6 %** — one CLI command exercises a narrow slice of the code the graph
describes. A broader workload (an MCP session over the read path) would raise the confirmed count. It
would not change the finding below.

## The finding that matters, and it is a negative one

The policy file calls part 2 *"the only part that can confirm or refute the `Inferred` tier
empirically."* **That premise is false, and this run is what shows it.**

Every `CALLS` edge in every graph on this machine is `Extracted`:

| graph | `CALLS` edges | tier 0 | tier 1 | tier 2 |
|---|---|---|---|---|
| `Brain` (probe, HEAD) | 2 138 | 2 138 | 0 | 0 |
| `Brain` (live, `43d88fc7`) | 2 111 | 2 111 | 0 | 0 |
| `sitecoreMuM` | 4 331 | 4 331 | 0 | 0 |
| `FPM-Optimizely` | 121 | 121 | 0 | 0 |

`CALLS` is produced only by the Roslyn semantic linker, and only with a resolved symbol. There is no
inferred call edge to refute — not in this corpus, not in any corpus indexed here.

The `Inferred` tier lives on other relations entirely: `SyntacticHeritage` (154), `UniqueNameMatch` (26),
`DocumentLink` (26), `AmbiguousNameMatch` (5). **None of those is observable by a CPU profiler.** A
class's base type is a static fact; no amount of running the program emits it as a call. Testing that
tier empirically needs a *static* oracle — resolving heritage and type references against the compiled
assemblies' real metadata and comparing — which is a different instrument from the one AP6 specified.

So part 2, as executed, validates the `Extracted` tier for `CALLS` and demonstrates that the tier it was
meant to test cannot be reached this way.

## Two attempts that produced nothing

Recorded because both failed silently in the way this project keeps getting caught by — an absent
signal read as a good signal.

1. **`dotnet-trace collect -- dotnet test`** traced the *launcher*, not the test host.
   `DOTNET_DefaultDiagnosticPortSuspend=1` is inherited by child processes, so `vstest.console` started
   suspended and never ran. The collector happily wrote **1,6 GB over five hours** — all of it idle
   samples of a waiting launcher, not one test frame in it. A large output file looked like progress.
2. **The graph was 8 commits behind the binary.** `shonkor.db` was indexed at `43d88fc7`, HEAD is
   `b177da6` (#453–#456, 19 files). Comparing a trace of HEAD against a graph of `43d88fc7` would have
   produced numbers that looked fine and meant nothing. Fixed structurally: the traced run now *is* the
   run that builds the compared graph.

## Reproduce

```
dotnet build Shonkor.slnx -c Release
dotnet-trace collect --format Speedscope --duration 00:15:00 -o <out>/trace.speedscope.json \
  -- <repo>/src/Shonkor.CLI/bin/Release/net10.0/shonkor.exe index <repo> -c <probe>/shonkor.json
ap6cmp <out>/trace.speedscope.speedscope.json <probe>/probe.db
```

The comparator is `scratchpad/ap6cmp` — ~120 lines, `Microsoft.Data.Sqlite` only, no dependency on
Shonkor's own assemblies so it cannot drift with them.
