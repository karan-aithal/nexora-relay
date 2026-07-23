# ADR-0002 — Target .NET 10 (LTS)

## Status
Accepted (Phase 0).

## Context
The project needs a single target framework for all C# projects. The audience is a
fuel-dispenser payments vendor whose production estates run conservative, long-support
stacks — the technology choice itself is part of the portfolio signal.

## Decision
Target **.NET 10 (LTS)**, `net10.0`, C# 14, set once in `Directory.Build.props`.

Windows-only adapter projects (`Adapters.Pcsc`, `Adapters.Serial`) target
`net10.0-windows` because they P/Invoke `winscard.dll` and use `System.IO.Ports` against a
com0com virtual null-modem. They are excluded from the Linux CI build via the
`OpenForecourt.CI.slnf` solution filter (see ADR-0001 and the CI workflow).

## Consequences
- **Positive:** Long support window matches industrial payment estates. C# 14 features
  (primary constructors, collection expressions) are available.
- **Positive:** One TFM to reason about; platform-specific code is isolated to two
  clearly-marked projects.
- **Negative:** A Linux-only developer cannot build the two Windows adapters. Accepted:
  the whole point of the ports design is that CI proves the system without them, and the
  in-process adapters cover the same contracts.

## Alternatives considered
- **.NET 8 (current LTS at time of writing on some machines).** Rejected: .NET 10 is the
  newer LTS and is the deliberately-conservative-but-current choice. Revisit only via a
  new ADR.
- **Multi-targeting every project.** Rejected: unnecessary complexity; only the two
  Windows adapters have a platform constraint.
