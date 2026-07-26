import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { OptView, PumpSnapshot } from '../api/models';
import { TickClock } from '../shared/clock';
import { elapsed, litres, money } from '../shared/format';

/**
 * One pump on the forecourt grid: state, live volume and value, grade, card brand and how
 * long the session has been running.
 *
 * The volume and value shown here are metered by the pump firmware and arrive over OFP-1 —
 * the tile renders numbers, it does not animate them.
 */
@Component({
  selector: 'app-pump-tile',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <article class="tile" [attr.data-state]="pump().state" [attr.data-testid]="'pump-' + pump().pumpId">
      <header>
        <span class="id">PUMP {{ pump().pumpId }}</span>
        <span class="state" data-testid="pump-state">{{ pump().state }}</span>
      </header>

      <div class="readout">
        <div class="volume">
          <span class="value" data-testid="pump-volume">{{ volume() }}</span>
          <span class="unit">L</span>
        </div>
        <div class="money">
          <span class="value" data-testid="pump-value">{{ value() }}</span>
          <span class="unit">{{ pump().currency }}</span>
        </div>
      </div>

      <dl class="meta">
        <div><dt>Grade</dt><dd>{{ pump().gradeCode ?? '—' }}</dd></div>
        <div><dt>Price</dt><dd>{{ price() }}/L</dd></div>
        <div><dt>Card</dt><dd>{{ pump().cardBrand ?? '—' }}</dd></div>
        <div><dt>Auth</dt><dd>{{ authorised() }}</dd></div>
        <div><dt>Elapsed</dt><dd>{{ sessionElapsed() }}</dd></div>
        <div><dt>Preset</dt><dd>{{ pump().authorisedMinor > 0 ? 'VALUE' : '—' }}</dd></div>
      </dl>

      <footer>
        <span class="chip" [class.chip--warn]="pump().link !== 'Connected'">LINK {{ pump().link }}</span>
        <span class="chip">FSM {{ pump().dispenserState }}</span>
        @if (pump().nozzleUp) {
          <span class="chip chip--live">NOZZLE UP</span>
        }
        @if (armedCrc()) {
          <span class="chip chip--fault">CRC ARMED</span>
        }
      </footer>

      @if (terminal(); as opt) {
        <p class="opt" data-testid="pump-opt">{{ opt.message }}</p>
      }

      <button type="button" class="link" [disabled]="!pump().transactionId" (click)="inspect.emit()">
        Inspect transaction
      </button>
    </article>
  `,
  styleUrl: './pump-tile.css',
})
export class PumpTile {
  private readonly clock = inject(TickClock);

  /** The pump to render. */
  readonly pump = input.required<PumpSnapshot>();

  /** The terminal serving this pump, when there is one. */
  readonly terminal = input<OptView | undefined>(undefined);

  /** Whether a CRC corruption is armed on this pump's next frame. */
  readonly armedCrc = input(false);

  /** Raised when the operator wants the transaction trace for this pump. */
  readonly inspect = output<void>();

  protected readonly volume = computed(() => litres(this.pump().dispensedMillilitres));
  protected readonly value = computed(() => money(this.pump().dispensedMinor));
  protected readonly price = computed(() => money(this.pump().unitPricePerLitreMinor));
  protected readonly authorised = computed(() =>
    this.pump().authorisedMinor > 0 ? money(this.pump().authorisedMinor) : '—',
  );
  protected readonly sessionElapsed = computed(() => elapsed(this.pump().startedAt, this.clock.now()));
}
