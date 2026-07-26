import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { TransactionView } from '../api/models';
import { money } from '../shared/format';

/**
 * The live transaction feed with a text filter.
 *
 * One row per transaction, updated in place as it moves Intent → HostRequestSent → Approved →
 * Completed. An event log would be more literal but far less useful: an operator watching a
 * forecourt wants to know the current state of each sale, not its history replayed.
 */
@Component({
  selector: 'app-transaction-feed',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe],
  template: `
    <header class="head">
      <h2>Transactions</h2>
      <input
        type="search"
        placeholder="filter by pump, status, auth code…"
        data-testid="tx-filter"
        [value]="filter()"
        (input)="onFilter($event)"
      />
      <span class="count">{{ filtered().length }} / {{ transactions().length }}</span>
    </header>

    <div class="scroll">
      <table>
        <thead>
          <tr>
            <th>Pump</th>
            <th>Status</th>
            <th class="num">Amount</th>
            <th>Auth</th>
            <th>Mode</th>
            <th>Updated</th>
          </tr>
        </thead>
        <tbody>
          @for (tx of filtered(); track tx.transactionId) {
            <tr
              [class.selected]="tx.transactionId === selected()"
              [attr.data-testid]="'tx-row'"
              (click)="select.emit(tx.transactionId)"
            >
              <td>{{ tx.pumpId }}</td>
              <td [attr.data-status]="tx.status">{{ tx.status }}</td>
              <td class="num">{{ amount(tx) }}</td>
              <td>{{ tx.authorisationCode ?? '—' }}</td>
              <td>{{ tx.offline ? 'OFFLINE' : 'ONLINE' }}</td>
              <td>{{ tx.updatedAt | date: 'HH:mm:ss' }}</td>
            </tr>
          } @empty {
            <tr class="empty"><td colspan="6">No transactions yet.</td></tr>
          }
        </tbody>
      </table>
    </div>
  `,
  styleUrl: './transaction-feed.css',
})
export class TransactionFeed {
  /** The feed contents, newest first. */
  readonly transactions = input.required<readonly TransactionView[]>();

  /** The currently inspected transaction id. */
  readonly selected = input<string | null>(null);

  /** Raised when a row is clicked. */
  readonly select = output<string>();

  protected readonly filter = signal('');

  protected readonly filtered = computed(() => {
    const needle = this.filter().trim().toLowerCase();
    if (!needle) {
      return this.transactions();
    }

    return this.transactions().filter((tx) =>
      [String(tx.pumpId), tx.status, tx.authorisationCode ?? '', tx.offline ? 'offline' : 'online', tx.transactionId]
        .join(' ')
        .toLowerCase()
        .includes(needle),
    );
  });

  protected amount(tx: TransactionView): string {
    return `${money(tx.amountMinor)} ${tx.currency}`;
  }

  protected onFilter(event: Event): void {
    this.filter.set((event.target as HTMLInputElement).value);
  }
}
