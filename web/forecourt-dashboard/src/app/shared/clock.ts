import { Injectable, OnDestroy, signal } from '@angular/core';

/**
 * A single one-second tick shared by every elapsed-time and countdown readout.
 *
 * One interval for the whole console rather than one per tile: eight pump timers plus eight
 * terminal countdowns is sixteen timers doing identical work, and they would drift apart
 * visibly. Reading the signal is what makes a component re-render each second.
 */
@Injectable({ providedIn: 'root' })
export class TickClock implements OnDestroy {
  private readonly handle = setInterval(() => this.now.set(Date.now()), 1000);

  /** Wall-clock milliseconds, updated once a second. */
  readonly now = signal(Date.now());

  ngOnDestroy(): void {
    clearInterval(this.handle);
  }
}
