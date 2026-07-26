import { ComponentFixture, TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { PUMP_STATES, PumpSnapshot, PumpState } from '../api/models';
import { PumpTile } from './pump-tile';

function snapshot(overrides: Partial<PumpSnapshot> = {}): PumpSnapshot {
  return {
    pumpId: 3,
    state: 'Idle',
    transactionId: null,
    authorisedMinor: 0,
    dispensedMillilitres: 0,
    currency: 'GBP',
    dispensedMinor: 0,
    gradeCode: 'U95',
    unitPricePerLitreMinor: 149,
    cardBrand: null,
    startedAt: null,
    link: 'Connected',
    dispenserState: 'Idle',
    nozzleUp: false,
    ...overrides,
  };
}

function render(pump: PumpSnapshot): ComponentFixture<PumpTile> {
  const fixture = TestBed.createComponent(PumpTile);
  fixture.componentRef.setInput('pump', pump);
  fixture.detectChanges();
  return fixture;
}

function text(fixture: ComponentFixture<PumpTile>, testId: string): string {
  const element = fixture.nativeElement.querySelector(`[data-testid="${testId}"]`) as HTMLElement | null;
  return element?.textContent?.trim() ?? '';
}

describe('PumpTile', () => {
  // CLAUDE.md Phase 6 asks for tile rendering across all PumpState values. State is the first
  // thing an operator reads, so every one must render its own name and carry its own colour.
  it.each(PUMP_STATES)('renders the %s state', (state: PumpState) => {
    const fixture = render(snapshot({ state }));

    expect(text(fixture, 'pump-state')).toBe(state);
    const tile = fixture.nativeElement.querySelector('.tile') as HTMLElement;
    expect(tile.getAttribute('data-state')).toBe(state);
  });

  it('shows the metered volume and value at the flow meter resolution', () => {
    const fixture = render(snapshot({ state: 'Dispensing', dispensedMillilitres: 12_345, dispensedMinor: 1839 }));

    expect(text(fixture, 'pump-volume')).toBe('12.345');
    expect(text(fixture, 'pump-value')).toBe('18.39');
  });

  it('flags a link that is not connected', () => {
    const fixture = render(snapshot({ link: 'Connecting' }));

    const chip = fixture.nativeElement.querySelector('.chip--warn') as HTMLElement;
    expect(chip.textContent).toContain('Connecting');
  });

  it('offers the trace only when there is a transaction to inspect', () => {
    const idle = render(snapshot());
    expect((idle.nativeElement.querySelector('button.link') as HTMLButtonElement).disabled).toBe(true);

    const busy = render(snapshot({ state: 'Dispensing', transactionId: '0198f0c0-0000-7000-8000-000000000001' }));
    expect((busy.nativeElement.querySelector('button.link') as HTMLButtonElement).disabled).toBe(false);
  });

  it('shows the terminal prompt when a terminal is serving the pump', () => {
    const fixture = TestBed.createComponent(PumpTile);
    fixture.componentRef.setInput('pump', snapshot({ state: 'Authorising' }));
    fixture.componentRef.setInput('terminal', {
      pumpId: 3,
      stage: 'PinRequired',
      message: 'Enter PIN',
      cardBrand: 'Visa',
      maskedPan: '411111******1111',
      cvm: 'OnlinePin',
      transactionId: null,
      deadlineAt: null,
    });
    fixture.detectChanges();

    expect(text(fixture, 'pump-opt')).toBe('Enter PIN');
  });

  it('marks a pump whose next frame will be corrupted', () => {
    const fixture = TestBed.createComponent(PumpTile);
    fixture.componentRef.setInput('pump', snapshot());
    fixture.componentRef.setInput('armedCrc', true);
    fixture.detectChanges();

    expect((fixture.nativeElement.querySelector('.chip--fault') as HTMLElement).textContent).toContain('CRC ARMED');
  });
});
