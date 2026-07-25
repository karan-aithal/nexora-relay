# TEST-ONLY — do not use

Every value in this directory is a published, synthetic **test** key (CLAUDE.md §7.2).

- **`bdk.hex`** — the Base Derivation Key `0123456789ABCDEFFEDCBA9876543210`. This is the
  Base Derivation Key from the canonical ANSI X9.24-1 DUKPT worked example, reproduced in
  countless implementations and test suites. It protects nothing real. With the initial KSN
  `FFFF9876543210E00000` it derives the IPEK `6AC292FAA1315B4D858AB3A3D7D5933A`, which the
  unit tests assert against.

Keys live here — never in `src/`, never in a config default, never in a Dockerfile
(CLAUDE.md §7.2). A real Base Derivation Key is injected into an HSM and never leaves it;
this file exists only so the simulator and its tests have something to derive from.
