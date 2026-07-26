import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { LinkStatus } from '../api/realtime';
import { TotalsView } from '../api/models';
import { money } from '../shared/format';

/**
 * Site totals and the end-of-day view.
 *
 * "End of day" on a forecourt is a reconciliation question: has everything that was authorised
 * been settled, and is anything still carried offline? So the readout that matters is not the
 * takings but the outstanding exposure — the value the site has approved on its own authority
 * and not yet confirmed with the acquirer.
 */
@Component({
  selector: 'app-totals-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="totals">
      <div class="stat">
        <span class="label">Settled</span>
        <span class="value" data-testid="totals-completed">{{ totals().completed }}</span>
      </div>
      <div class="stat">
        <span class="label">Value</span>
        <span class="value" data-testid="totals-value">{{ value() }} {{ totals().currency }}</span>
      </div>
      <div class="stat">
        <span class="label">Declined</span>
        <span class="value">{{ totals().declined }}</span>
      </div>
      <div class="stat">
        <span class="label">Reversed</span>
        <span class="value">{{ totals().reversed }}</span>
      </div>
      <div class="stat" [class.stat--warn]="totals().offlinePending > 0">
        <span class="label">Offline pending</span>
        <span class="value" data-testid="totals-offline">{{ totals().offlinePending }}</span>
      </div>
      <div class="stat" [class.stat--warn]="totals().offlineExposureMinor > 0">
        <span class="label">Exposure</span>
        <span class="value">{{ exposure() }}</span>
      </div>
      <div class="stat stat--end" [attr.data-eod]="reconciled()">
        <span class="label">End of day</span>
        <span class="value" data-testid="totals-eod">{{ reconciled() ? 'RECONCILED' : 'OUTSTANDING' }}</span>
      </div>
      <div class="stat stat--link" [attr.data-link]="status()">
        <span class="label">Feed</span>
        <span class="value" data-testid="link-status">{{ status() }}</span>
      </div>
    </section>
  `,
  styleUrl: './totals-bar.css',
})
export class TotalsBar {
  /** The site totals. */
  readonly totals = input.required<TotalsView>();

  /** The live feed's status, shown beside the totals so a stale board is obvious. */
  readonly status = input.required<LinkStatus>();

  protected readonly value = computed(() => money(this.totals().completedValueMinor));
  protected readonly exposure = computed(() => money(this.totals().offlineExposureMinor));

  // The day can be closed only when nothing is still carried on the site's own authority.
  protected readonly reconciled = computed(
    () => this.totals().offlinePending === 0 && this.totals().offlineExposureMinor === 0,
  );
}
