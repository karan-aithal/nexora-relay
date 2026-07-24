# TEST-ONLY — do not use

Every value in these card profiles is synthetic test data (CLAUDE.md section 7):

- **PANs** are from the reserved test ranges (`4111…`, `5555…`, `4000…`). No real
  cardholder data appears here or anywhere in this repository.
- **Cryptograms** (`9F26`) are fixed placeholder bytes. A real card computes the
  Application Cryptogram with a card key; this simulator returns a canned value because
  Phase 2 exercises the *message flow*, not cryptography. DUKPT and real key handling
  arrive in Phase 3.
- **AIDs** use the `A00000000398/0399…` style RID range for the simulated OpenForecourt
  scheme where they are not a published scheme AID.

These profiles are loaded by `OpenForecourt.VirtualCard.CardProfile.Load`.
