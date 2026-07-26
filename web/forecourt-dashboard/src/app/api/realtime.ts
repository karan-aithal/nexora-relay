import { InjectionToken, Injectable, computed, inject, signal } from '@angular/core';
import { FaultState, OptView, PumpSnapshot, SiteSnapshot, TotalsView, TransactionView } from './models';

/** How the dashboard's live feed is currently doing. */
export type LinkStatus = 'idle' | 'connecting' | 'connected' | 'reconnecting' | 'disconnected';

/**
 * The narrow slice of a SignalR connection this application actually uses.
 *
 * Declaring the port rather than depending on `HubConnection` directly is what lets the
 * reconnection tests run against a stub hub with no server and no sockets — the same
 * ports-and-adapters rule the backend follows (CLAUDE.md section 3), applied to the client.
 */
export interface RealtimeConnection {
  on(method: string, handler: (payload: unknown) => void): void;
  onreconnecting(handler: () => void): void;
  onreconnected(handler: () => void): void;
  onclose(handler: () => void): void;
  start(): Promise<void>;
  invoke<T>(method: string): Promise<T>;
}

/** Supplies the connection. Overridden in tests with a stub hub. */
export const REALTIME_CONNECTION = new InjectionToken<() => RealtimeConnection>('REALTIME_CONNECTION');

const EMPTY_FAULTS: FaultState = {
  hostLinkDown: false,
  hostLatencyMs: 0,
  forcedResponsePan: null,
  suspended: false,
  crcArmedPumps: [],
  poweredOffPumps: [],
};

const EMPTY_TOTALS: TotalsView = {
  currency: 'GBP',
  completed: 0,
  declined: 0,
  reversed: 0,
  offlinePending: 0,
  completedValueMinor: 0,
  offlineExposureMinor: 0,
};

/** How many transactions the live feed keeps client-side. */
const FEED_LIMIT = 200;

/**
 * The live half of the dashboard: one SignalR connection, all state in signals.
 *
 * Two rules make this correct across a dropped connection:
 *
 * 1. **Every frame is a whole entity, keyed.** A `PumpUpdated` frame carries a complete
 *    `PumpSnapshot`, not a delta, so applying frames is idempotent and a duplicate delivery
 *    after a reconnect changes nothing.
 * 2. **A reconnect always resynchronises.** On reconnect the client throws away what it holds
 *    and applies a fresh server snapshot, because it cannot know which frames it missed while
 *    away. Reconciling a gap you cannot see is guesswork; replacing the world is not.
 */
@Injectable({ providedIn: 'root' })
export class ForecourtRealtime {
  private readonly factory = inject(REALTIME_CONNECTION);

  private readonly pumpMap = signal<ReadonlyMap<number, PumpSnapshot>>(new Map());
  private readonly terminalMap = signal<ReadonlyMap<number, OptView>>(new Map());

  private connection: RealtimeConnection | null = null;

  /** The live feed's connection status. */
  readonly status = signal<LinkStatus>('idle');

  /** How many times the client has resynchronised against a server snapshot. */
  readonly resyncs = signal(0);

  /** Every pump, ordered by id. */
  readonly pumps = computed(() => [...this.pumpMap().values()].sort((a, b) => a.pumpId - b.pumpId));

  /** Every terminal, ordered by pump id. */
  readonly terminals = computed(() => [...this.terminalMap().values()].sort((a, b) => a.pumpId - b.pumpId));

  /** The transaction feed, newest first. */
  readonly transactions = signal<readonly TransactionView[]>([]);

  /** The armed fault set. */
  readonly faults = signal<FaultState>(EMPTY_FAULTS);

  /** Site totals. */
  readonly totals = signal<TotalsView>(EMPTY_TOTALS);

  /** Opens the feed. Safe to call once; subsequent calls are ignored. */
  async connect(): Promise<void> {
    if (this.connection) {
      return;
    }

    const connection = this.factory();
    this.connection = connection;

    connection.on('Snapshot', (payload) => this.applySnapshot(payload as SiteSnapshot));
    connection.on('PumpUpdated', (payload) => this.applyPump(payload as PumpSnapshot));
    connection.on('OptUpdated', (payload) => this.applyOpt(payload as OptView));
    connection.on('TransactionUpdated', (payload) => this.applyTransaction(payload as TransactionView));
    connection.on('FaultsUpdated', (payload) => this.faults.set(payload as FaultState));
    connection.on('TotalsUpdated', (payload) => this.totals.set(payload as TotalsView));

    connection.onreconnecting(() => this.status.set('reconnecting'));
    connection.onreconnected(() => void this.resync());
    connection.onclose(() => this.status.set('disconnected'));

    this.status.set('connecting');
    await connection.start();
    this.status.set('connected');
  }

  /**
   * Pulls an authoritative snapshot and replaces local state with it. Called on every
   * reconnect, and available to the operator as a manual "resync" for the same reason.
   */
  async resync(): Promise<void> {
    if (!this.connection) {
      return;
    }

    const snapshot = await this.connection.invoke<SiteSnapshot>('GetSnapshot');
    this.applySnapshot(snapshot);
    this.status.set('connected');
  }

  private applySnapshot(snapshot: SiteSnapshot): void {
    this.pumpMap.set(new Map(snapshot.pumps.map((p) => [p.pumpId, p])));
    this.terminalMap.set(new Map(snapshot.terminals.map((t) => [t.pumpId, t])));
    this.transactions.set([...snapshot.recentTransactions]);
    this.faults.set(snapshot.faults);
    this.totals.set(snapshot.totals);
    this.resyncs.update((n) => n + 1);
  }

  private applyPump(pump: PumpSnapshot): void {
    this.pumpMap.update((current) => new Map(current).set(pump.pumpId, pump));
  }

  private applyOpt(view: OptView): void {
    this.terminalMap.update((current) => new Map(current).set(view.pumpId, view));
  }

  private applyTransaction(tx: TransactionView): void {
    // Keyed upsert, newest first. A transaction moves through several states, so the feed
    // shows one row per transaction that updates in place rather than a row per event.
    this.transactions.update((current) => {
      const without = current.filter((t) => t.transactionId !== tx.transactionId);
      return [tx, ...without].slice(0, FEED_LIMIT);
    });
  }
}
