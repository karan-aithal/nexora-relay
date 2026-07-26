/**
 * The wire contracts, mirroring the site controller's `Contracts/Views.cs`,
 * `Opt/OptService.cs` and `Faults/FaultInjector.cs`.
 *
 * These are hand-written rather than generated: the committed OpenAPI document is the
 * source of truth for the shape, and keeping the mirror small and explicit means a
 * breaking backend change fails the TypeScript build instead of failing silently at
 * runtime. No `any` appears anywhere in this file or downstream of it.
 */

/** The pump lifecycle state, mirroring `PumpState`. */
export type PumpState =
  | 'Idle'
  | 'Authorising'
  | 'Authorised'
  | 'NozzleLifted'
  | 'Dispensing'
  | 'DispenseComplete'
  | 'Settling'
  | 'Error'
  | 'OutOfService';

/** Every `PumpState` value, for exhaustive rendering and tests. */
export const PUMP_STATES: readonly PumpState[] = [
  'Idle',
  'Authorising',
  'Authorised',
  'NozzleLifted',
  'Dispensing',
  'DispenseComplete',
  'Settling',
  'Error',
  'OutOfService',
];

/** One pump's live state. */
export interface PumpSnapshot {
  readonly pumpId: number;
  readonly state: PumpState;
  readonly transactionId: string | null;
  readonly authorisedMinor: number;
  readonly dispensedMillilitres: number;
  readonly currency: string;
  readonly dispensedMinor: number;
  readonly gradeCode: string | null;
  readonly unitPricePerLitreMinor: number;
  readonly cardBrand: string | null;
  readonly startedAt: string | null;
  readonly link: string;
  readonly dispenserState: string;
  readonly nozzleUp: boolean;
}

/** One journalled transaction. Carries a token reference, never a PAN. */
export interface TransactionView {
  readonly transactionId: string;
  readonly pumpId: number;
  readonly status: string;
  readonly state: string;
  readonly amountMinor: number;
  readonly currency: string;
  readonly authorisationCode: string | null;
  readonly offline: boolean;
  readonly createdAt: string;
  readonly updatedAt: string;
}

/** Site totals across the journal. */
export interface TotalsView {
  readonly currency: string;
  readonly completed: number;
  readonly declined: number;
  readonly reversed: number;
  readonly offlinePending: number;
  readonly completedValueMinor: number;
  readonly offlineExposureMinor: number;
}

/** What an outdoor payment terminal is showing. */
export interface OptView {
  readonly pumpId: number;
  readonly stage: string;
  readonly message: string;
  readonly cardBrand: string | null;
  readonly maskedPan: string | null;
  readonly cvm: string | null;
  readonly transactionId: string | null;
  readonly deadlineAt: string | null;
}

/** The currently armed faults. */
export interface FaultState {
  readonly hostLinkDown: boolean;
  readonly hostLatencyMs: number;
  readonly forcedResponsePan: string | null;
  readonly suspended: boolean;
  readonly crcArmedPumps: readonly number[];
  readonly poweredOffPumps: readonly number[];
}

/** The full snapshot a (re)connecting dashboard resynchronises against. */
export interface SiteSnapshot {
  readonly pumps: readonly PumpSnapshot[];
  readonly recentTransactions: readonly TransactionView[];
  readonly terminals: readonly OptView[];
  readonly faults: FaultState;
  readonly totals: TotalsView;
}

/** One fuel grade on sale. */
export interface GradeView {
  readonly code: string;
  readonly name: string;
  readonly pricePerLitreMinor: number;
}

/** A test card the simulator can present. */
export interface CardProfileView {
  readonly id: string;
  readonly name: string;
  readonly kind: string;
  readonly brand: string;
}

/** Static site configuration, read once at start-up. */
export interface SiteInfoView {
  readonly currency: string;
  readonly pumpCount: number;
  readonly floorLimitMinor: number;
  readonly firmwarePumps: boolean;
  readonly grades: readonly GradeView[];
  readonly cards: readonly CardProfileView[];
}

/** One decoded name/value pair inside a trace step. */
export interface TraceField {
  readonly name: string;
  readonly value: string;
}

/** The kinds of exchange a trace step can be. */
export type TraceKind = 'apdu' | 'iso8583' | 'pump' | 'lifecycle';

/** One observable exchange in a transaction's life. */
export interface TraceStep {
  readonly seq: number;
  readonly kind: TraceKind;
  readonly label: string;
  readonly elapsedMs: number;
  readonly durationMs: number;
  readonly hex: string | null;
  readonly fields: readonly TraceField[];
}

/** A whole transaction trace. */
export interface TransactionTraceView {
  readonly transactionId: string;
  readonly startedAt: string;
  readonly totalMs: number;
  readonly steps: readonly TraceStep[];
}

/** What the OPT answered. */
export interface OptResult {
  readonly accepted: boolean;
  readonly decision: string;
  readonly transactionId: string | null;
  readonly message: string;
}

/** The acquirer's answer to a deliberately duplicated request. */
export interface DuplicateResult {
  readonly transactionId: string;
  readonly outcome: string;
  readonly authorisationCode: string | null;
}

/** How the card was presented at the terminal. */
export type EntryMode = 'Contact' | 'Contactless' | 'MagstripeFallback';
