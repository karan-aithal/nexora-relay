import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  input,
  viewChild,
} from '@angular/core';
import { OptView } from '../api/models';

/**
 * The OPT idle screen: an advertising loop that plays while the terminal is waiting for a card
 * and pauses for the duration of a transaction.
 *
 * A real outdoor terminal does exactly this — the media loop is the attract screen, and it
 * yields to the payment flow so the customer's attention is on the prompts and the amount.
 * The pause is driven by an `effect` over the terminal's stage signal rather than by an event
 * handler, so the video follows terminal state no matter what caused it to change: the card
 * simulator here, another operator's dashboard, or a timeout on the server.
 */
@Component({
  selector: 'app-media-loop',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="screen" [attr.data-idle]="isIdle()">
      <video
        #video
        data-testid="opt-video"
        src="opt-loop.mp4"
        muted
        loop
        playsinline
        preload="auto"
        aria-label="Terminal advertising loop"
      ></video>
      <div class="overlay">
        <span class="pump">OPT · PUMP {{ terminal()?.pumpId ?? '—' }}</span>
        <span class="prompt" data-testid="opt-prompt">{{ terminal()?.message ?? 'Insert or tap card' }}</span>
      </div>
    </div>
  `,
  styleUrl: './media-loop.css',
})
export class MediaLoop {
  private readonly video = viewChild.required<ElementRef<HTMLVideoElement>>('video');

  /** The terminal whose screen this is. */
  readonly terminal = input<OptView | undefined>(undefined);

  protected readonly isIdle = computed(() => {
    const stage = this.terminal()?.stage ?? 'Idle';
    return stage === 'Idle' || stage === 'Finished';
  });

  constructor() {
    effect(() => {
      const element = this.video().nativeElement;
      if (this.isIdle()) {
        // play() rejects when autoplay is blocked or the asset is missing; either way the
        // still frame is a perfectly good idle screen, so the rejection is not an error.
        void element.play().catch(() => undefined);
      } else {
        element.pause();
      }
    });
  }
}
