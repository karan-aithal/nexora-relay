import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { TraceKind, TraceStep, TransactionTraceView, TransactionView } from '../api/models';
import { hexBytes, money } from '../shared/format';

/** The drawer's filter tabs. */
type TraceTab = 'all' | TraceKind;

/**
 * The transaction detail drawer: the full trace of one sale.
 *
 * This is the screen the whole project points at. It shows the literal APDU exchange with the
 * card, the literal ISO 8583 request and response decoded field by field, the OFP-1 commands
 * that armed and settled the dispenser, and a timing waterfall over the lot — all of it
 * captured by the components that did the work, not reconstructed afterwards.
 *
 * The raw hex is shown beside the decoded view because they answer different questions: the
 * hex is what a logic analyser would see, the decoded view is what it means. Sensitive fields
 * are masked in the decoded view by the protocol's own metadata, never by this component.
 */
@Component({
  selector: 'app-trace-drawer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <aside class="drawer" data-testid="trace-drawer">
      <header>
        <div>
          <h2>Transaction trace</h2>
          @if (transaction(); as tx) {
            <p class="sub">
              Pump {{ tx.pumpId }} · {{ money(tx.amountMinor) }} {{ tx.currency }} · {{ tx.status }}
              @if (tx.offline) { · <span class="offline">OFFLINE</span> }
            </p>
          }
          <p class="sub mono">{{ transactionId() }}</p>
        </div>
        <button type="button" class="close" (click)="closed.emit()" aria-label="Close trace">✕</button>
      </header>

      @if (trace(); as t) {
        <nav class="tabs">
          @for (tab of tabs; track tab) {
            <button
              type="button"
              [class.active]="active() === tab"
              [attr.data-testid]="'trace-tab-' + tab"
              (click)="active.set(tab)"
            >
              {{ tab }} ({{ countOf(tab) }})
            </button>
          }
          <span class="total">{{ t.totalMs.toFixed(1) }} ms total</span>
        </nav>

        <ol class="steps">
          @for (step of visible(); track step.seq) {
            <li [attr.data-kind]="step.kind" [attr.data-testid]="'trace-step'">
              <div class="row">
                <span class="seq">{{ step.seq }}</span>
                <span class="kind">{{ step.kind }}</span>
                <span class="label">{{ step.label }}</span>
                <span class="at">+{{ step.elapsedMs.toFixed(1) }} ms</span>
              </div>

              <!-- Timing waterfall: offset from the first step, width for the exchange itself. -->
              <div class="bar" [title]="step.durationMs.toFixed(1) + ' ms'">
                <span
                  class="fill"
                  [style.margin-left.%]="offsetPercent(step, t)"
                  [style.width.%]="widthPercent(step, t)"
                ></span>
              </div>

              @if (step.hex) {
                <pre class="hex">{{ hexBytes(step.hex) }}</pre>
              }

              @if (step.fields.length) {
                <dl class="fields">
                  @for (field of step.fields; track field.name + field.value) {
                    <div><dt>{{ field.name }}</dt><dd>{{ field.value }}</dd></div>
                  }
                </dl>
              }
            </li>
          } @empty {
            <li class="empty">No steps of this kind.</li>
          }
        </ol>
      } @else {
        <p class="empty" data-testid="trace-empty">
          No trace retained for this transaction. Traces are a bounded, in-memory diagnostic; the
          durable record is the journal.
        </p>
      }
    </aside>
  `,
  styleUrl: './trace-drawer.css',
})
export class TraceDrawer {
  /** The trace to render, or null when none was retained. */
  readonly trace = input<TransactionTraceView | null>(null);

  /** The transaction the trace belongs to, for the header. */
  readonly transaction = input<TransactionView | null>(null);

  /** The id being inspected, shown even when no trace survives. */
  readonly transactionId = input<string>('');

  /** Raised when the drawer should close. */
  readonly closed = output<void>();

  protected readonly tabs: readonly TraceTab[] = ['all', 'apdu', 'iso8583', 'pump', 'lifecycle'];
  protected readonly active = signal<TraceTab>('all');

  protected readonly visible = computed(() => {
    const steps = this.trace()?.steps ?? [];
    const tab = this.active();
    return tab === 'all' ? steps : steps.filter((s) => s.kind === tab);
  });

  protected readonly money = money;
  protected readonly hexBytes = hexBytes;

  protected countOf(tab: TraceTab): number {
    const steps = this.trace()?.steps ?? [];
    return tab === 'all' ? steps.length : steps.filter((s) => s.kind === tab).length;
  }

  protected offsetPercent(step: TraceStep, trace: TransactionTraceView): number {
    return trace.totalMs > 0 ? (step.elapsedMs / trace.totalMs) * 100 : 0;
  }

  // A zero-duration step (an instantaneous marker) still needs to be visible, so bars have a
  // floor width. Without it the pump commands and lifecycle markers vanish from the waterfall.
  protected widthPercent(step: TraceStep, trace: TransactionTraceView): number {
    const raw = trace.totalMs > 0 ? (step.durationMs / trace.totalMs) * 100 : 0;
    return Math.max(raw, 0.8);
  }
}
