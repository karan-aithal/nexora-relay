# How to run this build

## Setup

```bash
mkdir openforecourt && cd openforecourt
git init
mkdir -p docs/phases docs/adr docs/walkthroughs scripts
# copy CLAUDE.md to the repo root
# copy docs/phases/*.md into docs/phases/
git add . && git commit -m "docs: project context and phase specifications"
```

Open the folder in VS Code, start Claude Code.

## The kickoff message

Paste this once, at the very start:

```
Read CLAUDE.md in full, then read docs/phases/00-scaffold.md.

Before writing any code:
1. Summarise the architecture back to me in your own words, so I can confirm
   you have it right.
2. List every ambiguity or underspecified detail you found in CLAUDE.md and
   in the Phase 0 spec.
3. Give me a short implementation plan for Phase 0 only.

Do not start implementing until I approve the plan.
```

That approval gate matters. If the architecture summary is wrong, you find out in two
minutes rather than three days.

## Every subsequent session

```
Read CLAUDE.md and docs/phases/NN-<name>.md.
Confirm the previous phase's Definition of Done is met, then plan Phase NN.
Do not begin implementing until I approve the plan.
```

## Session hygiene

- **One phase per session.** Clear context between phases — the phase file plus
  CLAUDE.md is all the context needed, and stale context is what causes drift.
- **Commit before moving on.** Never start a phase on a dirty tree.
- If a phase is running long, split it and add a `NNa` / `NNb` file rather than
  letting one session sprawl.
- When Claude proposes scope not in the phase file, redirect it to `docs/backlog.md`.

## Useful mid-phase prompts

```
Run the demo script and paste the actual output. Do not describe expected output.
```

```
List every place in this phase where you were uncertain about the specification.
For each, tell me what you assumed and how confident you are.
```

```
Review the code you just wrote as a hostile senior reviewer. Find the three
weakest points. Do not fix them yet.
```

```
I am going to be asked about this in an interview. Explain <file/function>
line by line as if I have never seen it.
```

That last one is the important one — see the note below.

## Suggested schedule (5-week full-time sprint)

| Days | Phases |
|---|---|
| 1 | Phase 0 |
| 2–5 | Phase 1 — ISO 8583. Do not rush this one. |
| 6–10 | Phase 2 — card layer |
| 11–13 | Phase 3 — crypto |
| 14–18 | Phase 4 — firmware |
| 19–22 | Phase 5 — site controller |
| 23–25 | Phase 6 — front end |
| 26–30 | Phase 7 — hardening and docs |
| spare | Phase 8, or buffer (you will need buffer) |

Phases 1 and 4 are the ones that overrun. Protect their time by keeping Phase 6 lean.

## One honest note on "generate everything, I review"

That is a reasonable choice for a five-week sprint — you will not finish otherwise, and
reviewing 20,000 lines critically is itself a real skill. But the risk is specific and
worth naming: in an interview you will be asked to explain the DUKPT key derivation, the
bitmap parser, or your journal write-ordering, and generated code you skimmed will not
survive that.

The walkthrough documents in `CLAUDE.md` section 8 exist for exactly this reason. Treat
the self-quiz at the end of each one as a real gate: if you cannot answer all five
questions without looking, do not move to the next phase. Re-read the code, ask Claude
to explain it line by line, and rewrite one function yourself from scratch.

There are three pieces where writing it yourself, by hand, is worth the extra day
each — they are the ones most likely to be probed:

1. The ISO 8583 bitmap encoder/decoder
2. DUKPT IPEK derivation and key advancement
3. The journal write-ordering and crash-recovery logic

Let Claude generate them first, read them until they make sense, then delete and
rewrite from memory. It is the fastest way to actually own the code.
