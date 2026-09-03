# AP6 — what this benchmark measures, and what it cannot

Written **before** the first number exists, so the constraints are part of the corpus rather than a
footnote in a later report.

## The two questions

1. **Positioning.** Does the graph beat `ripgrep` + reading files, and on which kinds of task? The
   mandate predicts a win only on whole-program questions and not on local navigation, and states that
   confirming this is a positioning finding, not a failure. The task set is therefore built so it *can*
   produce that answer — it includes tasks where ripgrep should win. A corpus that cannot fail is not a
   measurement.
2. **Ground truth.** Do the graph's predicted `CALLS` edges match the ones a real run actually takes?
   This is the only part that can confirm or refute the `Inferred` tier empirically, which is why it is
   built first — part 1 needs its output as an answer key.

## Decisions taken without ratification

The three open questions in #459 were not answered before "mach so", so they are decided here and
flagged as mine:

| | decision | why |
|---|---|---|
| instrumentation | `dotnet-trace` sampling | no IL rewriting, no code change, repeatable by anyone with the SDK. The cost is that it **samples**: absence of an edge is far weaker evidence than presence of one |
| answer key | mechanically checkable only | a task whose correctness I judge is a task I grade myself. Tasks assert a symbol set or a file list, nothing prose |
| corpus | `Brain` (this repository) | its graph is the only one with **zero unattributed edges** (#428), and its test suite is what part 2 traces. `sitecoreMuM` is more realistic in scale and would make part 2 impossible — there is no runnable suite for it here |

## What every number here is a lower bound on

The measured graph is **partially repaired**. Recording it up front so no later report can quietly omit
it:

- `implementations_of` prints name guesses since #402 and shows no trust tier — **#429**
- syntactic `IMPLEMENTS`/`EXTENDS` carry 1 680 dangling targets on a real solution — **#405**
- `IMPORTS` carries 2 839 unfollowable targets — **#440**
- `IMPLEMENTS_MEMBER` carries 2 954 targets naming indexed files that hold no such node — **#436**

A traversal that cannot reach a node scores as "did not find it", so every precision figure understates
what a repaired graph would do. It does **not** overstate: nothing here inflates a hit.

## Three readings that are not allowed

**"Not observed" is not "refuted."** A sampling profiler misses calls, and a test suite does not
exercise everything. Part 2 reports three outcomes — confirmed, contradicted, unobserved — and never
folds the third into the second. Collapsing them would be the same mistake that made a cached restore
layer look like a clean one (#389) and a silent staleness check look like a fresh graph (#449).

**No aggregate score.** Forbidden by the mandate, and the finding is per task type anyway. A single
number would hide exactly the distinction the positioning question is about.

**No comparison across graphs.** Numbers from `Brain` say nothing about `sitecoreMuM`; the tooling
differs (`Brain` has no plugins active) and so does the shape.

## Provenance of the numbers themselves

Each result file records: the graph's `indexedRevision` (#449), its `toolchainFingerprint` (#408), and
the count of edges by `ProvenanceReason` (#428). A benchmark whose input state is not recorded is a
benchmark that cannot be repeated — which is what made the pre-#408 measurements worthless.
