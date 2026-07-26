import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ForecourtApi } from './api/api';
import { OptView, SiteInfoView, TransactionTraceView } from './api/models';
import { ForecourtRealtime } from './api/realtime';
import { MediaLoop } from './opt/media-loop';
import { CardSimulator } from './panels/card-simulator';
import { FaultConsole } from './panels/fault-console';
import { SimulatorPanel } from './panels/simulator-panel';
import { PumpTile } from './pumps/pump-tile';
import { TotalsBar } from './totals/totals-bar';
import { TraceDrawer } from './transactions/trace-drawer';
import { TransactionFeed } from './transactions/transaction-feed';

/**
 * The console shell: totals across the top, the pump grid and transaction feed in the middle,
 * the operator panels down the side, and the trace drawer over the right when a transaction is
 * being inspected.
 *
 * All live state comes from one place — {@link ForecourtRealtime} — and every command goes out
 * over REST. The shell holds only what is genuinely local to this browser: which transaction is
 * open, which trace has been fetched for it, and which terminal the OPT screen is showing.
 */
@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PumpTile, TransactionFeed, TraceDrawer, TotalsBar, SimulatorPanel, CardSimulator, FaultConsole, MediaLoop],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  private readonly api = inject(ForecourtApi);

  protected readonly live = inject(ForecourtRealtime);

  protected readonly site = signal<SiteInfoView | null>(null);
  protected readonly selectedId = signal<string | null>(null);
  protected readonly trace = signal<TransactionTraceView | null>(null);
  protected readonly optPump = signal(1);

  protected readonly selectedTransaction = computed(
    () => this.live.transactions().find((t) => t.transactionId === this.selectedId()) ?? null,
  );

  protected readonly selectedTerminal = computed(() => this.terminalFor(this.optPump()));
  protected readonly grades = computed(() => this.site()?.grades ?? []);
  protected readonly cards = computed(() => this.site()?.cards ?? []);
  protected readonly latest = computed(() => this.live.transactions()[0]);

  constructor() {
    void this.start();
  }

  protected terminalFor(pumpId: number): OptView | undefined {
    return this.live.terminals().find((t) => t.pumpId === pumpId);
  }

  protected crcArmed(pumpId: number): boolean {
    return this.live.faults().crcArmedPumps.includes(pumpId);
  }

  /** Opens the drawer for a transaction and fetches its trace. */
  protected async inspect(transactionId: string | null): Promise<void> {
    if (!transactionId) {
      return;
    }

    this.selectedId.set(transactionId);
    this.trace.set(null);
    try {
      this.trace.set(await this.api.trace(transactionId));
    } catch {
      // A trace that has been evicted is not an error: the drawer says so and shows the id.
      this.trace.set(null);
    }
  }

  protected closeDrawer(): void {
    this.selectedId.set(null);
    this.trace.set(null);
  }

  protected onOptPump(event: Event): void {
    this.optPump.set(Number((event.target as HTMLSelectElement).value));
  }

  protected resync(): void {
    void this.live.resync();
  }

  private async start(): Promise<void> {
    this.site.set(await this.api.site());
    await this.live.connect();
  }
}
