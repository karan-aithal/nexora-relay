import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { FaultState, OptView, PumpSnapshot, SiteSnapshot, TotalsView, TransactionView } from './models';
import { ForecourtRealtime, REALTIME_CONNECTION, RealtimeConnection } from './realtime';

/**
 * A stub hub: no sockets, no server. It lets a test push any frame and, crucially, drive the
 * reconnection lifecycle by hand — which is the only way to assert the resynchronisation
 * behaviour deterministically.
 */
class StubHub implements RealtimeConnection {
  private readonly handlers = new Map<string, (payload: unknown) => void>();
  private reconnecting: (() => void) | null = null;
  private reconnected: (() => void) | null = null;
  private closed: (() => void) | null = null;

  /** Snapshots served by GetSnapshot, oldest first. */
  readonly snapshots: SiteSnapshot[] = [];

  /** How many times the client asked for a snapshot. */
  invocations = 0;

  on(method: string, handler: (payload: unknown) => void): void {
    this.handlers.set(method, handler);
  }

  onreconnecting(handler: () => void): void {
    this.reconnecting = handler;
  }

  onreconnected(handler: () => void): void {
    this.reconnected = handler;
  }

  onclose(handler: () => void): void {
    this.closed = handler;
  }

  start(): Promise<void> {
    return Promise.resolve();
  }

  invoke<T>(method: string): Promise<T> {
    this.invocations++;
    if (method !== 'GetSnapshot') {
      throw new Error(`unexpected invoke: ${method}`);
    }

    return Promise.resolve(this.snapshots[this.snapshots.length - 1] as T);
  }

  emit(method: string, payload: unknown): void {
    this.handlers.get(method)?.(payload);
  }

  dropLink(): void {
    this.reconnecting?.();
  }

  restoreLink(): void {
    this.reconnected?.();
  }

  close(): void {
    this.closed?.();
  }
}

const TOTALS: TotalsView = {
  currency: 'GBP',
  completed: 2,
  declined: 0,
  reversed: 0,
  offlinePending: 1,
  completedValueMinor: 4500,
  offlineExposureMinor: 1000,
};

const FAULTS: FaultState = {
  hostLinkDown: false,
  hostLatencyMs: 0,
  forcedResponsePan: null,
  suspended: false,
  crcArmedPumps: [],
  poweredOffPumps: [],
};

function pump(pumpId: number, overrides: Partial<PumpSnapshot> = {}): PumpSnapshot {
  return {
    pumpId,
    state: 'Idle',
    transactionId: null,
    authorisedMinor: 0,
    dispensedMillilitres: 0,
    currency: 'GBP',
    dispensedMinor: 0,
    gradeCode: 'U95',
    unitPricePerLitreMinor: 149,
    cardBrand: null,
    startedAt: null,
    link: 'Connected',
    dispenserState: 'Idle',
    nozzleUp: false,
    ...overrides,
  };
}

function terminal(pumpId: number, stage: string): OptView {
  return {
    pumpId,
    stage,
    message: stage,
    cardBrand: null,
    maskedPan: null,
    cvm: null,
    transactionId: null,
    deadlineAt: null,
  };
}

function transaction(id: string, status: string): TransactionView {
  return {
    transactionId: id,
    pumpId: 1,
    status,
    state: 'Authorised',
    amountMinor: 2500,
    currency: 'GBP',
    authorisationCode: '123456',
    offline: false,
    createdAt: '2026-07-26T09:00:00Z',
    updatedAt: '2026-07-26T09:00:01Z',
  };
}

function snapshot(pumps: PumpSnapshot[], transactions: TransactionView[] = []): SiteSnapshot {
  return {
    pumps,
    recentTransactions: transactions,
    terminals: pumps.map((p) => terminal(p.pumpId, 'Idle')),
    faults: FAULTS,
    totals: TOTALS,
  };
}

describe('ForecourtRealtime', () => {
  let hub: StubHub;
  let live: ForecourtRealtime;

  beforeEach(() => {
    hub = new StubHub();
    TestBed.configureTestingModule({
      providers: [{ provide: REALTIME_CONNECTION, useValue: () => hub }],
    });
    live = TestBed.inject(ForecourtRealtime);
  });

  it('applies the snapshot the server sends on connect', async () => {
    await live.connect();
    hub.emit('Snapshot', snapshot([pump(1), pump(2)], [transaction('a', 'Approved')]));

    expect(live.status()).toBe('connected');
    expect(live.pumps().map((p) => p.pumpId)).toEqual([1, 2]);
    expect(live.transactions()).toHaveLength(1);
    expect(live.totals().offlinePending).toBe(1);
  });

  it('applies live frames as keyed upserts', async () => {
    await live.connect();
    hub.emit('Snapshot', snapshot([pump(1), pump(2)]));

    hub.emit('PumpUpdated', pump(2, { state: 'Dispensing', dispensedMillilitres: 4200 }));
    hub.emit('OptUpdated', terminal(2, 'PinRequired'));
    hub.emit('FaultsUpdated', { ...FAULTS, hostLinkDown: true });

    expect(live.pumps()[1].state).toBe('Dispensing');
    expect(live.terminals()[1].stage).toBe('PinRequired');
    expect(live.faults().hostLinkDown).toBe(true);
  });

  // A transaction moves through several states. The feed must show one row per transaction
  // that updates in place, not one row per event.
  it('updates a transaction in place and keeps the newest first', async () => {
    await live.connect();
    hub.emit('Snapshot', snapshot([pump(1)]));

    hub.emit('TransactionUpdated', transaction('a', 'Intent'));
    hub.emit('TransactionUpdated', transaction('b', 'Intent'));
    hub.emit('TransactionUpdated', transaction('a', 'Completed'));

    expect(live.transactions().map((t) => t.transactionId)).toEqual(['a', 'b']);
    expect(live.transactions()[0].status).toBe('Completed');
  });

  // The behaviour this whole design exists for: the client cannot know which frames it missed
  // while disconnected, so on reconnect it discards local state and replaces it wholesale.
  it('resynchronises against a server snapshot after a reconnect', async () => {
    hub.snapshots.push(snapshot([pump(1), pump(2)]));
    await live.connect();
    hub.emit('Snapshot', hub.snapshots[0]);

    hub.dropLink();
    expect(live.status()).toBe('reconnecting');

    // While the dashboard was away, pump 2 finished a fuelling and a third pump appeared.
    hub.snapshots.push(
      snapshot([pump(1), pump(2, { state: 'DispenseComplete', dispensedMillilitres: 30_000 }), pump(3)]),
    );
    hub.restoreLink();
    await Promise.resolve();
    await Promise.resolve();

    expect(hub.invocations).toBe(1);
    expect(live.status()).toBe('connected');
    expect(live.pumps().map((p) => p.pumpId)).toEqual([1, 2, 3]);
    expect(live.pumps()[1].state).toBe('DispenseComplete');
    expect(live.resyncs()).toBe(2); // the connect snapshot, then the reconnect resync
  });

  it('reports a closed connection so a stale board is obvious', async () => {
    await live.connect();
    hub.close();

    expect(live.status()).toBe('disconnected');
  });

  it('connects only once however many times connect is called', async () => {
    await live.connect();
    await live.connect();

    hub.snapshots.push(snapshot([pump(1)]));
    await live.resync();

    expect(hub.invocations).toBe(1);
  });
});
