import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { ForecourtApi } from '../api/api';
import { FaultState, PumpSnapshot, TransactionView } from '../api/models';

/**
 * The fault-injection console.
 *
 * Nothing here fakes an outcome. Severing the host link makes the acquirer genuinely
 * unreachable, so the prober marks it down and the site falls back to offline authorisation.
 * Forcing a response code sends the test PAN the acquirer's rules decline, so the decline
 * comes back over the wire. Corrupting a CRC damages the framed bytes after framing, so the
 * firmware rejects the frame and the manager retransmits. Cutting power kills the firmware
 * process. Every consequence on screen is the system's own.
 */
@Component({
  selector: 'app-fault-console',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel">
      <h2>Fault injection</h2>

      <div class="row">
        <button
          type="button"
          class="danger"
          data-testid="fault-host-link"
          [class.armed]="faults().hostLinkDown"
          (click)="run(api.hostFault({ down: !faults().hostLinkDown }))"
        >
          {{ faults().hostLinkDown ? 'Restore host link' : 'Kill host link' }}
        </button>
        <button
          type="button"
          class="danger"
          data-testid="fault-suspend"
          [class.armed]="faults().suspended"
          (click)="run(faults().suspended ? api.resumeSite() : api.suspendSite())"
        >
          {{ faults().suspended ? 'Resume site controller' : 'Suspend mid-authorisation' }}
        </button>
      </div>

      <label class="field">
        <span>Host latency {{ latency() }} ms</span>
        <input
          type="range"
          min="0"
          max="8000"
          step="250"
          data-testid="fault-latency"
          [value]="latency()"
          (input)="onLatency($event)"
        />
      </label>

      <label class="field">
        <span>Force acquirer response</span>
        <select data-testid="fault-response" (change)="onResponse($event)">
          @for (code of responseCodes; track code.value) {
            <option [value]="code.value">{{ code.label }}</option>
          }
        </select>
      </label>

      <label class="field">
        <span>Pump</span>
        <select data-testid="fault-pump" [value]="pumpId()" (change)="onPump($event)">
          @for (pump of pumps(); track pump.pumpId) {
            <option [value]="pump.pumpId">Pump {{ pump.pumpId }}</option>
          }
        </select>
      </label>

      <div class="row">
        <button type="button" data-testid="fault-nozzle" class="danger" (click)="run(api.nozzle(pumpId(), 'down'))">
          Drop nozzle
        </button>
        <button type="button" data-testid="fault-crc" class="danger" (click)="run(api.corruptCrc(pumpId()))">
          Corrupt frame CRC
        </button>
        <button type="button" data-testid="fault-power" class="danger" (click)="togglePower()">
          {{ isOff() ? 'Restore power' : 'Power-cut pump' }}
        </button>
      </div>

      <div class="row">
        <button
          type="button"
          data-testid="fault-duplicate"
          class="danger"
          [disabled]="!latestTransaction()"
          (click)="duplicate()"
        >
          Force duplicate transaction
        </button>
      </div>

      <p class="status" data-testid="fault-status">{{ status() }}</p>
    </section>
  `,
  styleUrl: './panel.css',
})
export class FaultConsole {
  protected readonly api = inject(ForecourtApi);

  /** Every pump, for the selector. */
  readonly pumps = input.required<readonly PumpSnapshot[]>();

  /** The armed fault set, mirrored live from the server. */
  readonly faults = input.required<FaultState>();

  /** The newest transaction, which is what "force duplicate" re-sends. */
  readonly latestTransaction = input<TransactionView | undefined>(undefined);

  protected readonly responseCodes = [
    { value: '00', label: '00 — approve' },
    { value: '51', label: '51 — insufficient funds' },
    { value: '05', label: '05 — do not honour' },
    { value: '54', label: '54 — expired card' },
    { value: '91', label: '91 — issuer unavailable (silent)' },
  ];

  protected readonly pumpId = signal(1);
  protected readonly latency = signal(0);
  protected readonly status = signal('No faults armed.');

  protected isOff(): boolean {
    return this.faults().poweredOffPumps.includes(this.pumpId());
  }

  protected onPump(event: Event): void {
    this.pumpId.set(Number((event.target as HTMLSelectElement).value));
  }

  protected onLatency(event: Event): void {
    const value = Number((event.target as HTMLInputElement).value);
    this.latency.set(value);
    void this.run(this.api.hostFault({ latencyMs: value }));
  }

  protected onResponse(event: Event): void {
    void this.run(this.api.hostFault({ responseCode: (event.target as HTMLSelectElement).value }));
  }

  protected togglePower(): void {
    void this.run(this.api.pumpPower(this.pumpId(), this.isOff() ? 'on' : 'off'));
  }

  protected async duplicate(): Promise<void> {
    const tx = this.latestTransaction();
    if (!tx) {
      return;
    }

    try {
      const result = await this.api.duplicate(tx.transactionId);
      this.status.set(`Acquirer answered the duplicate with: ${result.outcome}.`);
    } catch (error) {
      this.status.set(error instanceof Error ? error.message : 'Duplicate could not be sent.');
    }
  }

  protected async run(work: Promise<unknown>): Promise<void> {
    try {
      await work;
      this.status.set('Fault armed.');
    } catch (error) {
      this.status.set(error instanceof Error ? error.message : 'Fault could not be armed.');
    }
  }
}
