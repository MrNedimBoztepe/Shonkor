# AP6 part 1 — the graph against ripgrep + reading files

Executed under `ap6-precision-evidence-policy.md`. Read that first.

## Design

Twelve tasks, each one a **real merged commit**. The answer key is the set of `.cs` files that commit
actually touched — mechanical, independent of every arm, and invisible to all of them. Six tasks are
whole-program (5–13 files), six are local (2–3 files); the split exists so the corpus *can* produce the
mandate's predicted negative.

The seed identifier per task is chosen mechanically — the basename of the largest changed source file —
not by me. Every arm is a program, so the run repeats:

| arm | what it does | what "bytes" counts |
|---|---|---|
| `grep` | `grep -rlw <seed> --include=*.cs src tests plugins` | total size of the files it points at — what an agent must read to decide |
| `edit_plan` | MCP `edit_plan` with the bare name | size of the tool response |
| `blast_name` | MCP `blast_radius` with the bare name | size of the tool response |
| `blast_file` | MCP `blast_radius` with the seed's **file path** | size of the tool response |

`blast_file` exists because `blast_radius` on a bare type name resolves to the type's *constructor*
(#462) and reports a false all-clear. Addressing the class by node id would be the obvious escape hatch
and does not work either (#463). The file path is the only route left, so it is the arm that measures
the **graph** rather than the resolver.

Graph: `probe.db`, `indexedRevision b177da6…` (= HEAD), `toolchainFingerprint c631cb23…`, 3 454 nodes /
8 486 edges, 0 unattributed. `grep -rlw` was used in place of `ripgrep`; on the control seed both return
the identical 32 files, and `rg` is a shell function unavailable to a script.

## Result

| arm | group | key files | hits | correct | recall | precision | bytes |
|---|---|---|---|---|---|---|---|
| `grep` | whole-program | 47 | 194 | 18 | **38,3 %** | 9,3 % | 2 890 660 |
| `edit_plan` | whole-program | 47 | 6 | 6 | 12,8 % | 100 % | **1 609** |
| `blast_name` | whole-program | 47 | 6 | 6 | 12,8 % | 100 % | 2 729 |
| `blast_file` | whole-program | 47 | 307 | 22 | **46,8 %** | 7,2 % | 1 199 254 |
| `grep` | local | 14 | 60 | 11 | **78,6 %** | 18,3 % | 1 086 898 |
| `edit_plan` | local | 14 | 39 | 10 | 71,4 % | 25,6 % | **10 914** |
| `blast_name` | local | 14 | 215 | 11 | **78,6 %** | 5,1 % | 493 011 |
| `blast_file` | local | 14 | 219 | 11 | **78,6 %** | 5,0 % | 578 109 |

The 100 % precision of `edit_plan` / `blast_name` on whole-program tasks is **not a strength**. Both
returned exactly one file — the definition — because of #462. Returning only the thing you were asked
about is trivially precise and useless.

## What this says about the positioning claim

**The mandate's prediction is inverted by the measurement.** It predicts a win on whole-program
questions and no win on local navigation. Measured:

- **Local navigation is where the graph clearly wins.** `edit_plan` reaches 71,4 % recall at **10,9 KB**
  against grep's 78,6 % at **1,09 MB** — a hundredfold cheaper for seven percentage points less recall,
  and at better precision (25,6 % vs 18,3 %). That is the "precise context, not a pile of files" claim
  actually holding.
- **On whole-program questions the graph loses as shipped** — 12,8 % against grep's 38,3 %, because the
  bare name resolves to a constructor (#462). Routed around that defect it edges ahead: 46,8 % against
  38,3 %, at 2,4× fewer bytes. The advantage is real but narrow, and today it is unreachable through the
  interface an agent actually uses.

**Nothing here wins outright.** The best recall measured anywhere is 78,6 %; on whole-program tasks no
arm reaches half the answer key. An agent using either instrument alone will miss files.

## Limits — read before quoting a number

- **Twelve tasks, one repository.** These are not general figures.
- **The key is what a commit chose, not the only correct answer.** A file an arm returns that the commit
  did not touch is not necessarily wrong, so precision **understates** every arm. Recall is the primary
  signal; precision is only comparable *between* arms, not against 100 %.
- **The seed is in the key by construction** on most tasks, so every arm gets one hit for free.
- **`ReadTools` is a file name, not an identifier**, so `grep` scored 0 on that task — a consequence of
  the mechanical seed rule, not of grep. Excluding it, grep's whole-program recall is 42,9 % (18/42) and
  `blast_file`'s is 50,0 % (21/42). The ordering does not change.
- **"Bytes" are two different currencies.** For `grep` it is the source an agent must read; for the MCP
  arms it is the response. The graph response *names* files the agent still has to open before editing.
  The comparison is the cost of getting a candidate set, not the cost of the whole task.
- **The graph is partially repaired** (#429, #405, #440, #436), so its numbers are a lower bound.

## Defects this run surfaced

Three, all found while building the arms rather than by looking for them:

- **#462** — a bare type name resolves to the constructor; `edit_plan` answers *"No reference sites —
  safe to change in isolation"* for a class with 71 incident edges that `grep` finds in 32 files.
  Reproduces on the live graph.
- **#463** — `blast_radius` cannot consume the node ids it emits, removing the workaround for #462.
- **#461** — `mcp -c <config>` is parsed and ignored; the server silently served an empty graph and
  three of four tools did not say so.

Two of the three are the same shape as the failures already catalogued in this project: an absent signal
read as a good signal.

## Reproduce

```
scratchpad/ap6/run/run.sh <scratchpad>        # emits results.tsv
```

Tasks in `scratchpad/ap6/run/tasks.txt` (kind|commit|seed), seed→node map in `handles.txt`, MCP driver
in `scratchpad/ap6b` (JSON-RPC over stdio, ~60 lines, no dependency on Shonkor's assemblies).
