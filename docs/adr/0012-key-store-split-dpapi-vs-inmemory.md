# 0012 — Key store: DPAPI in production, in-memory blocked in Release

## Context

`ISecureKeyStore` (defined in Phase 0) needs real implementations in Phase 3. CLAUDE.md §3
fixes the split: a Windows DPAPI-backed store and an in-memory store that **refuses to run in a
Release build**. The point is that unprotected key material in process memory must never be a
production path, and that must be enforced by the compiler, not by a reviewer noticing.

## Decision

- **`DpapiKeyStore`** — encrypts each key with Windows DPAPI (`ProtectedData`, CurrentUser
  scope) and writes one protected blob per label into a directory. The label is SHA-256'd into
  the file name so an arbitrary label cannot escape the directory. Marked
  `[SupportedOSPlatform("windows")]`; it is the real production-shaped path (the counterpart to
  the physical PC/SC reader) and is never exercised by CI, which runs Linux.
- **`InMemoryKeyStore`** — a plain concurrent dictionary, but its constructor throws in a
  Release build via `#if !DEBUG`. Tests use it in Debug; a Release build cannot construct it.
- The DPAPI dependency is `System.Security.Cryptography.ProtectedData`, a Microsoft package that
  builds cross-platform (so CI compiles it) and throws at runtime only if actually called off
  Windows. Justified in the commit and the csproj comment.

## Consequences

- CI (Linux, often Release) proves the Release guard: `InMemoryKeyStore` throws, and a companion
  test asserts exactly that when compiled without `DEBUG`. In Debug the store round-trips and the
  guard test is compiled out.
- The Windows path exists and is documented but is not run in CI — consistent with how the
  physical adapters are treated (ADR 0008). Its behaviour is proven by construction, not by a
  Linux test.
- DPAPI is at-rest protection under the OS user key, **not** an HSM. The threat model states this
  plainly: it stops another OS user reading the key file; it does not make the key unextractable
  to a process running as that user.

## Alternatives considered

- **A runtime configuration flag to select the store.** The whole point is that Release must be
  unable to use the in-memory store *at all*; a runtime flag could be misconfigured. A compile
  symbol cannot.
- **Encrypt the in-memory store's contents too.** Pointless — the process holds the key to
  decrypt them; it would be protection theatre. The honest control is: this store does not run in
  Release, full stop.
- **A cross-platform `libsecret`/keychain store.** Out of scope; the locked decision is DPAPI on
  Windows with the in-memory store for tests. Another OS-native store slots in at the same seam
  later if needed.
