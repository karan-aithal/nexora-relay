import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { ForecourtApi } from '../api/api';
import { CardProfileView, EntryMode, OptView, PumpSnapshot } from '../api/models';
import { TickClock } from '../shared/clock';
import { countdown } from '../shared/format';

/**
 * The card simulator: pick a test card, choose how it is presented, and drive the OPT.
 *
 * Presenting a card runs the real EMV kernel against the real virtual card profile — the same
 * JSON the Phase 2 unit tests use. The PIN pad appears only when the card's CVM list actually
 * asks for online PIN, because the kernel decides that, not this component.
 */
@Component({
  selector: 'app-card-simulator',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel">
      <h2>Card simulator</h2>

      <label class="field">
        <span>Pump</span>
        <select data-testid="card-pump" [value]="pumpId()" (change)="onPump($event)">
          @for (pump of pumps(); track pump.pumpId) {
            <option [value]="pump.pumpId">Pump {{ pump.pumpId }}</option>
          }
        </select>
      </label>

      <label class="field">
        <span>Card</span>
        <select data-testid="card-profile" [value]="cardId()" (change)="onCard($event)">
          @for (card of cards(); track card.id) {
            <option [value]="card.id">{{ card.name }} — {{ card.brand }}</option>
          }
        </select>
      </label>

      <label class="field">
        <span>Entry mode</span>
        <select data-testid="card-entry" [value]="entryMode()" (change)="onEntry($event)">
          @for (mode of entryModes; track mode) {
            <option [value]="mode">{{ mode }}</option>
          }
        </select>
      </label>

      <label class="field">
        <span>Pre-authorisation ({{ (amountMinor() / 100).toFixed(2) }})</span>
        <input
          type="number"
          min="100"
          step="100"
          data-testid="card-amount"
          [value]="amountMinor()"
          (input)="onAmount($event)"
        />
      </label>

      <div class="row">
        <button type="button" data-testid="card-present" (click)="present()">Present card</button>
        <button type="button" (click)="run(api.cancelOpt(pumpId()))">Cancel</button>
        <button type="button" (click)="run(api.idleOpt(pumpId()))">Idle screen</button>
      </div>

      @if (terminal(); as opt) {
        <p class="status" data-testid="opt-message">[{{ opt.stage }}] {{ opt.message }}</p>
        @if (opt.maskedPan) {
          <p class="status">PAN {{ opt.maskedPan }} · CVM {{ opt.cvm }}</p>
        }
        @if (remaining() !== null) {
          <p class="status" data-testid="opt-countdown">Timeout in {{ remaining() }}s</p>
        }

        @if (opt.stage === 'PinRequired') {
          <p class="pin" data-testid="pin-display">{{ masked() }}</p>
          <div class="keypad">
            @for (key of keys; track key) {
              <button type="button" [attr.data-testid]="'pin-' + key" (click)="press(key)">{{ key }}</button>
            }
            <button type="button" (click)="pin.set('')">CLR</button>
            <button type="button" data-testid="pin-enter" (click)="submitPin()">ENTER</button>
          </div>
        }
      }

      <p class="status" data-testid="card-status">{{ status() }}</p>
    </section>
  `,
  styleUrl: './panel.css',
})
export class CardSimulator {
  protected readonly api = inject(ForecourtApi);
  private readonly clock = inject(TickClock);

  /** Every pump, for the selector. */
  readonly pumps = input.required<readonly PumpSnapshot[]>();

  /** The test cards on offer. */
  readonly cards = input.required<readonly CardProfileView[]>();

  /** Terminal states, keyed by pump. */
  readonly terminals = input.required<readonly OptView[]>();

  protected readonly entryModes: readonly EntryMode[] = ['Contact', 'Contactless', 'MagstripeFallback'];
  protected readonly keys = ['1', '2', '3', '4', '5', '6', '7', '8', '9', '0'];

  protected readonly pumpId = signal(1);
  protected readonly cardId = signal('');
  protected readonly entryMode = signal<EntryMode>('Contact');
  protected readonly amountMinor = signal(5000);
  protected readonly pin = signal('');
  protected readonly status = signal('Ready.');

  protected readonly terminal = computed(() => this.terminals().find((t) => t.pumpId === this.pumpId()));
  protected readonly remaining = computed(() => countdown(this.terminal()?.deadlineAt ?? null, this.clock.now()));
  protected readonly masked = computed(() => '•'.repeat(this.pin().length).padEnd(4, '·'));

  protected onPump(event: Event): void {
    this.pumpId.set(Number((event.target as HTMLSelectElement).value));
  }

  protected onCard(event: Event): void {
    this.cardId.set((event.target as HTMLSelectElement).value);
  }

  protected onEntry(event: Event): void {
    this.entryMode.set((event.target as HTMLSelectElement).value as EntryMode);
  }

  protected onAmount(event: Event): void {
    this.amountMinor.set(Number((event.target as HTMLInputElement).value));
  }

  protected press(key: string): void {
    if (this.pin().length < 12) {
      this.pin.update((current) => current + key);
    }
  }

  protected async present(): Promise<void> {
    const card = this.cardId() || this.cards()[0]?.id;
    if (!card) {
      this.status.set('No card profiles are loaded.');
      return;
    }

    this.pin.set('');
    try {
      const result = await this.api.presentCard(this.pumpId(), card, this.amountMinor(), this.entryMode());
      this.status.set(result.message);
    } catch (error) {
      this.status.set(error instanceof Error ? error.message : 'Card could not be presented.');
    }
  }

  protected async submitPin(): Promise<void> {
    try {
      const result = await this.api.enterPin(this.pumpId(), this.pin());
      this.status.set(result.message);
    } catch (error) {
      this.status.set(error instanceof Error ? error.message : 'PIN was refused.');
    } finally {
      this.pin.set('');
    }
  }

  protected async run(work: Promise<unknown>): Promise<void> {
    try {
      await work;
      this.status.set('Sent.');
    } catch (error) {
      this.status.set(error instanceof Error ? error.message : 'Command failed.');
    }
  }
}
