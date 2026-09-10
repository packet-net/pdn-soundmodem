# MS110D Phase B3 entry — full-budget Poor re-baseline (2026-07-24, merged B2 code `2ef90c7`)

The phase-b-plan §4 whole-table re-measure before the B3 grind. Canonical seeds, full §5.3 budgets, [poor-rebaseline.log](poor-rebaseline.log):

| WN | B0 baseline | Full budget on B2 | State |
|---|---|---|---|
| 3 | 8.7E-3 → 0 (B0) | **0 errors / 3.19M** | **at mask** |
| 13 | 6.2E-4 | **4.62E-6, bound 7.63E-6** | **at mask (canonical)** |
| 4 | 1.91E-5 | **4.99E-6, bound 7.99E-6** | **at mask** |
| 6 | 1.29E-1 | 4.41E-2 (118c/14r) | tail-dominated |
| 2 | 3.67E-2 | 1.32E-2 | tail-dominated |
| 1 | 2.85E-2 | 2.31E-2 | tail-dominated |
| 5 | 2.2E-2 | 2.10E-2 (253c/11r) | tail-dominated |
| 0 | 8.1E-2 | 1.12E-1 | B3.5 detector family |
| 7 | 4.66E-1 | 4.58E-1 | B3.3 |
| 8 | 4.96E-1 | 4.97E-1 | B3.4 |

**The central finding: below 8PSK, mean performance is essentially solved and the catastrophic-burst tail is everything.** WN5 smoked at 5.6E-6 over 8 bursts yet measures 2.10E-2 over 24 — a minority of bursts fail wholesale (turbo-reverted blocks: 11r/14r) while the rest run clean; WN2 likewise (8.56E-5 smoke → 1.32E-2 at 124 bursts). Same signature as WN13's disjoint-seed tail from the B2 gate runs. B3.1/B3.2's first job is the tail autopsy: which bursts die, and why — the leading suspect being a tracking failure mode invisible to probe-correlation magnitude (a symmetry-locked rotation reads healthy).
