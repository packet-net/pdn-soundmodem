# W6 — gate/verdict decision + program closeout (WN8 redesign program)

Registered 2026-07-31, after W5 merged (PR #139: Poor WN8 2.90E-4 canonical / 2.27E-2 disjoint at full §5.3 budgets). This is the escalation rule's written reassessment, taken as the program plan §4 requires: options on the table, cost of each, one pre-committed closure iteration, then the verdict.

## The reassessment

**Where the gap lives (measured, W5b2 battery):** the disjoint family's entire excess is four single-block non-convergences (98,065 of 98,065+65-class; without them ≈1.5E-5 ≈ 1.5× mask); canonical's is dominated by one burst leaking 1,029 (without it ≈5E-5). The residual is a single mechanism — the deepest fade-lottery blocks failing to terminate under the shipped schedule — not a diffuse floor.

**Options weighed:** (a) close now as exit (ii) — zero cost, banks 1,711×/22×; (b) one closure iteration on the measured mechanism; (c) further levers (fade-adaptive refit cadence, moment observables) — unregistered scope, deferred regardless.

**The pre-committed closure iteration (W6a): alternate-schedule fallback on revert.** W5b1's schedule table is banked evidence that the soft-first and hard-first basins differ per block (hard-only solved canonical b6 exactly where soft left 40–60; soft+refit solved b3/b8 where hard left 36/62). When the shipped soft-first schedule fails to terminate (the revert path), the block reruns once under the hard-first schedule (pure hard rungs, the same convergence-gated refit at its handover); a termination there is accepted on the same fixed-point/cycle evidence. This is the repo's own diversity-bank pattern applied per block, label-free by construction, firing only on blocks that would otherwise revert to coin-flip — cost is extra rungs on rare failures only.

**Pre-committed decision rule.** After W6a: corpses must not regress (canonical ≤112-class, disjoint ≤32-class), pins byte-identical, hermetic 0-failed, then the full battery:
- If **both families land §5.3-green at the 1E-5 mask** on the 4.33M-bit budgets (≥30 errors → direct ≤1E-5, else the 97.5% bound clears) AND the B4 false-red criterion is satisfiable at a registered default budget → **exit (i)**: `Poor_Channel_Mask_Gate` arms WN8, the nightly set gains the point, the ledger row forwards (sim-only annotation per plan §6).
- Otherwise → **exit (ii)**, immediately and without further levers: the program closes on the measured numbers; `MS110D_POOR_GATED` expected-red values re-bank to them; the closeout records the remaining measured levers (per-block schedule diversity beyond two, fade-adaptive refit, the slow-fade edge) for any successor.
- Either exit: program closeout section in this file, plan.md amendment, banked-negative amendments (B3.10 "immovable by this equalizer+chain class" and B3.4 "no label-free crossing" formally annotated with the W1b/W3/W5 measurements), umbrella issue #130 closed with the verdict.

**Budget.** ~60 lines (fallback schedule), corpses + pins + hermetic, one battery (~4 h).

## Measurements

(after the runs)
