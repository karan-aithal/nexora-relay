# Walkthrough 00 — Scaffold and Ports

## 1. Design rationale

Phase 0 builds the skeleton every later phase hangs off, and nothing more: the solution
layout, the port interfaces, the domain value types, CI, and the demo. **Zero business
logic** — no ISO 8583, no EMV, no crypto, no firmware.

The shape of the whole system is the ports-and-adapters design (ADR-0001). Everything the
system touches at its edges — a card reader, a pump link, the acquiring host, key storage,
the journal, the clock, the token vault — is an interface in `OpenForecourt.Abstractions`,
a project with **zero dependencies**. Concrete adapters live elsewhere and are chosen by
configuration. That one decision is what makes the system testable on Linux CI with no
hardware, and what makes "swap the simulator for a real ACR122U" a config change.

Two things get locked in now because they are load-bearing for the security story:

- **Money and Volume are integers, never floating point.** `Money(long Minor, string
  CurrencyCode)`, `Volume(long MilliLitres)`. Currency reconciliation and totalizer
  summation must be exact; binary/decimal floating point is not.
- **`Pan` masks by default and reveals only through an explicit method.** This is the
  enforcement point for "cleartext PAN never leaks into logs", and it is guaranteed by a
  test, not by discipline (CLAUDE.md section 7.3).

The port interfaces carry their *failure semantics* in XML docs, because the contract is
the point — every adapter, simulated or real, must behave the same way on timeout, card
removal, or reconnect. The most important of these is `IHostConnection`'s timeout-vs-late-
response rule: a reply that arrives after the timeout must never authorise an
already-abandoned transaction.

## 2. The three hardest parts

### 2.1 `Pan.ToString()` — masking that can't over-expose

File: `src/OpenForecourt.Abstractions/Domain/Pan.cs`.

The masking policy is "first 6 + last 4, rest starred", but a naive implementation leaks
on short inputs. If you always show 6 leading and 4 trailing, a 14-digit PAN would expose
`6 + 4 = 10` real digits with only 4 masked — and worse, if the PAN were 9 digits the two
windows would *overlap* and you would print more digits than exist. The code defends
against both:

```csharp
int len = _digits.Length;
int lead = Math.Min(6, len);          // never ask for more leading than exist
int trail = Math.Min(4, len - lead);  // trailing window cannot reach into the leading one
int masked = len - lead - trail;      // whatever is left in the middle
```

`lead` is clamped to the length. `trail` is clamped to *what remains after `lead`*, which
is the line that prevents the two windows overlapping — the trailing window can only
consume digits the leading window did not. `masked` is then whatever is left over.

```csharp
if (masked <= 0)
{
    return new string('*', Math.Max(0, len - 1)) + (len > 0 ? _digits[^1].ToString() : string.Empty);
}
```

If there is nothing left to mask (a short value where lead+trail already covers everything)
we do **not** fall back to showing the whole thing — that would defeat masking. Instead we
hide all but the last digit. This is the branch a hostile reviewer probes: "what does your
masker do for a 6-digit input?" — the answer is `*****d`, not `dddddd`.

```csharp
return string.Concat(
    _digits.AsSpan(0, lead),
    new string('*', masked),
    _digits.AsSpan(len - trail, trail));
```

`AsSpan` avoids allocating substrings just to concatenate them. The test
`ToString_never_exposes_more_than_first_6_and_last_4` counts leading digits before the
first `*` and trailing digits after the last `*` and asserts `≤ 6` and `≤ 4` across Visa,
Mastercard, Amex (15-digit), Discover and Diners (14-digit) test PANs — so the invariant
is checked, not assumed.

### 2.2 The purity test — proving Abstractions has no dependencies

File: `tests/OpenForecourt.Abstractions.UnitTests/AbstractionsPurityTests.cs`.

"Abstractions has zero dependencies" is the sentence the whole architecture rests on, so
it is asserted two ways:

```csharp
var forbidden = Abstractions
    .GetReferencedAssemblies()
    .Where(a => !IsBaseClassLibrary(a.Name))
    .Select(a => a.Name)
    .ToArray();
Assert.True(forbidden.Length == 0, ...);
```

This reflects over the *actual referenced assemblies* of the compiled Abstractions DLL and
fails if anything that is not the base class library appears. `IsBaseClassLibrary` allow-
lists `System.*`, `System.Private.CoreLib`, `System.Runtime`, `netstandard` and
`Microsoft.CSharp`. The moment someone adds a `ProjectReference` or a NuGet package to
Abstractions, a non-System assembly shows up and this test goes red.

The second test uses NetArchTest to specifically forbid the infrastructure frameworks the
contract layer must never see — ASP.NET, SQLite (both `Microsoft.Data.Sqlite` and the EF
provider), and RabbitMQ:

```csharp
Types.InAssembly(Abstractions).Should()
    .NotHaveDependencyOnAny("Microsoft.AspNetCore", "Microsoft.Data.Sqlite", ...)
```

Belt and braces: the reflection test catches *any* new dependency; the NetArchTest names
the specific villains from CLAUDE.md section 4 so a failure reads as "you leaked ASP.NET
into Abstractions", which is the message a future contributor needs.

### 2.3 Excluding the Windows adapters from Linux CI

Files: the two `Adapters.*` csprojs, `OpenForecourt.CI.slnf`, `.github/workflows/ci.yml`.

`PcscCardReader` P/Invokes `winscard.dll`; `SerialPumpTransport` uses a com0com virtual
COM port. Both are Windows-only *production* paths, not test mocks. They are given a
platform-specific target framework:

```xml
<TargetFramework>net10.0-windows</TargetFramework>
```

On Linux that TFM cannot build, which is exactly the honest behaviour we want — you should
not be able to pretend the Windows path built on Linux. So the Linux CI job never asks it
to. `OpenForecourt.CI.slnf` is a solution *filter* listing every project **except** those
two:

```json
{ "solution": { "path": "OpenForecourt.sln",
    "projects": [ ...everything but Adapters.Pcsc and Adapters.Serial... ] } }
```

CI restores/builds/tests the filter, not the full solution. A Windows developer opens the
full `OpenForecourt.sln` and gets all 13 projects including the two adapters. The subtlety
worth stating in an interview: the `.slnf` had to reference a classic `.sln`. .NET 10's
`dotnet new sln` now defaults to the XML `.slnx` format, which the solution-filter loader
rejected with an internal MSBuild failure — the fix was `dotnet new sln --format sln` to
force the boring, well-supported classic format.

## 3. What was rejected and why

- **Conditional compilation for simulation** (`#if SIMULATED`). Rejected — see ADR-0001.
  It couples business logic to simulation and guarantees drift.
- **`decimal` for Money.** Rejected: even decimal invites accidental fractional-minor-unit
  bugs and mixed-type arithmetic; integer minor units are unambiguous.
- **Making `PumpFrame`/`HostResponse`/`FinancialRequest` fully-specified now.** Deferred:
  their real fields depend on the pump protocol (Phase 4) and ISO 8583 mapping (Phase 1).
  Phase 0 defines the minimal envelope so the port signatures compile; fleshing them out
  early would be guessing.
- **A web SDK for `SiteController` now.** Deferred to Phase 5. A bare `Microsoft.NET.Sdk`
  classlib stub keeps Phase 0 at zero logic; converting to `Sdk.Web` would force a
  `Program.cs` host bootstrap that this phase explicitly excludes.
- **Suppressing analyzers globally to dodge warnings-as-errors.** Rejected. Two analyzer
  rules were handled narrowly instead: CA1000 (static factory on the generic `Result`) is
  suppressed on that one type with a written justification; CA1707 (underscores in test
  names) is disabled only under `tests/**` in `.editorconfig`, because
  `Method_condition_result` naming is the near-universal xUnit convention.

## 4. Self-quiz

1. A 9-digit value is wrapped in `Pan`. Walk through `lead`, `trail`, `masked` and state
   exactly what `ToString()` returns and why it cannot over-expose.
2. Why is `Money` an integer count of minor units rather than a `decimal`, and where would
   a `decimal` first bite you in a real settlement flow?
3. The purity test reflects over referenced assemblies *and* uses NetArchTest. What does
   each catch that the other does not?
4. On `IHostConnection`, distinguish a timeout from a late response. What must an adapter
   do with a reply whose request already timed out, and why is treating it as an approval
   dangerous?
5. Why do `Adapters.Pcsc` and `Adapters.Serial` target `net10.0-windows`, and what would
   break if they were plain `net10.0` and left in the Linux CI build?

## 5. Known weaknesses (simplifications vs a production system)

- **Message records are placeholders.** `PumpFrame`, `HostResponse`, `FinancialRequest`
  carry a fraction of their eventual fields. Intentional for Phase 0.
- **`ITokenVault` signatures are inferred**, not taken from a spec — the phase file names
  the port but not its methods. `Tokenize`/`Detokenize` is a reasonable minimal contract;
  it may grow (e.g. token format, key rotation) in the crypto phase.
- **No adapter implementations exist**, so the port *contracts* are documented but not yet
  proven by a conformance test suite. Those arrive with the first real adapters.
- **`Pan` accepts any digit string** — no Luhn check, no length validation. Deliberate:
  validation belongs to the card/EMV layer, not the value type, and test PANs must be
  accepted as-is.
- **The purity test's BCL allow-list is name-prefix based.** A malicious package named
  `System.Evil` would slip through. Acceptable for a portfolio repo; a stricter check would
  pin exact assembly identities.
