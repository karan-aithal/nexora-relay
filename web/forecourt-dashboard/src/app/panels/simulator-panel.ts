import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { ForecourtApi } from '../api/api';
import { GradeView, PumpSnapshot } from '../api/models';

/**
 * The operator side of the forecourt: the physical actions a customer or an attendant takes.
 *
 * Every control here reaches the pump firmware. Lifting the nozzle calls the same
 * `ofp_pump_nozzle_up` entry point a holster sensor would; changing the flow rate changes how
 * many millilitres the simulated flow meter delivers per tick; selecting a grade pushes a new
 * unit price into the dispenser, so the value it meters changes with it.
 */
@Component({
  selector: 'app-simulator-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel">
      <h2>Forecourt simulator</h2>

      <label class="field">
        <span>Pump</span>
        <select data-testid="sim-pump" [value]="pumpId()" (change)="onPump($event)">
          @for (pump of pumps(); track pump.pumpId) {
            <option [value]="pump.pumpId">Pump {{ pump.pumpId }} — {{ pump.state }}</option>
          }
        </select>
      </label>

      <div class="row">
        <button type="button" data-testid="sim-nozzle-up" (click)="run(api.nozzle(pumpId(), 'up'))">
          Lift nozzle
        </button>
        <button type="button" data-testid="sim-nozzle-down" (click)="run(api.nozzle(pumpId(), 'down'))">
          Replace nozzle
        </button>
      </div>

      <label class="field">
        <span>Grade</span>
        <select data-testid="sim-grade" (change)="onGrade($event)">
          @for (grade of grades(); track grade.code) {
            <option [value]="grade.code">{{ grade.name }} — {{ (grade.pricePerLitreMinor / 100).toFixed(2) }}/L</option>
          }
        </select>
      </label>

      <label class="field">
        <span>Flow {{ flow() }} mL/tick</span>
        <input
          type="range"
          min="10"
          max="600"
          step="10"
          data-testid="sim-flow"
          [value]="flow()"
          (input)="onFlow($event)"
        />
      </label>

      <div class="row">
        <button type="button" (click)="run(api.suspendPump(pumpId()))">Pause dispense</button>
        <button type="button" (click)="run(api.resumePump(pumpId()))">Resume dispense</button>
      </div>

      <p class="status" data-testid="sim-status">{{ status() }}</p>
    </section>
  `,
  styleUrl: './panel.css',
})
export class SimulatorPanel {
  protected readonly api = inject(ForecourtApi);

  /** Every pump, for the selector. */
  readonly pumps = input.required<readonly PumpSnapshot[]>();

  /** The grades on sale. */
  readonly grades = input.required<readonly GradeView[]>();

  protected readonly pumpId = signal(1);
  protected readonly flow = signal(100);
  protected readonly status = signal('Ready.');

  protected onPump(event: Event): void {
    this.pumpId.set(Number((event.target as HTMLSelectElement).value));
  }

  protected onGrade(event: Event): void {
    void this.run(this.api.grade(this.pumpId(), (event.target as HTMLSelectElement).value));
  }

  protected onFlow(event: Event): void {
    const value = Number((event.target as HTMLInputElement).value);
    this.flow.set(value);
    void this.run(this.api.flow(this.pumpId(), value));
  }

  // Commands are fire-and-report: the effect arrives over the live feed, so all this needs to
  // do is surface a failure rather than leave the operator guessing.
  protected async run(work: Promise<unknown>): Promise<void> {
    try {
      await work;
      this.status.set('Sent.');
    } catch (error) {
      this.status.set(error instanceof Error ? error.message : 'Command failed.');
    }
  }
}
