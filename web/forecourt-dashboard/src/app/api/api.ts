import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  DuplicateResult,
  EntryMode,
  FaultState,
  OptResult,
  SiteInfoView,
  TotalsView,
  TransactionTraceView,
  TransactionView,
} from './models';

/**
 * The REST half of the dashboard: everything the operator *does*.
 *
 * Commands go over HTTP and their effects come back over the SignalR feed rather than as
 * return values. That split is deliberate — it means the UI never has two sources of truth
 * for pump state, and a change made by another operator (or by the firmware itself) reaches
 * every open dashboard through exactly the same path as one made here.
 */
@Injectable({ providedIn: 'root' })
export class ForecourtApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/v1';

  site(): Promise<SiteInfoView> {
    return firstValueFrom(this.http.get<SiteInfoView>(`${this.base}/site`));
  }

  transactions(): Promise<TransactionView[]> {
    return firstValueFrom(this.http.get<TransactionView[]>(`${this.base}/transactions`));
  }

  totals(): Promise<TotalsView> {
    return firstValueFrom(this.http.get<TotalsView>(`${this.base}/totals`));
  }

  trace(transactionId: string): Promise<TransactionTraceView> {
    return firstValueFrom(
      this.http.get<TransactionTraceView>(`${this.base}/transactions/${transactionId}/trace`),
    );
  }

  // --- outdoor payment terminal ---------------------------------------------------------

  presentCard(pumpId: number, cardId: string, amountMinor: number, entryMode: EntryMode): Promise<OptResult> {
    return firstValueFrom(
      this.http.post<OptResult>(`${this.base}/opt/${pumpId}/card`, { cardId, amountMinor, entryMode }),
    );
  }

  enterPin(pumpId: number, pin: string): Promise<OptResult> {
    return firstValueFrom(this.http.post<OptResult>(`${this.base}/opt/${pumpId}/pin`, { pin }));
  }

  cancelOpt(pumpId: number): Promise<unknown> {
    return firstValueFrom(this.http.post(`${this.base}/opt/${pumpId}/cancel`, {}));
  }

  idleOpt(pumpId: number): Promise<unknown> {
    return firstValueFrom(this.http.post(`${this.base}/opt/${pumpId}/idle`, {}));
  }

  // --- forecourt simulator panel --------------------------------------------------------

  nozzle(pumpId: number, action: 'up' | 'down'): Promise<unknown> {
    return firstValueFrom(this.http.post(`${this.base}/simulator/pumps/${pumpId}/nozzle/${action}`, {}));
  }

  flow(pumpId: number, millilitresPerTick: number): Promise<unknown> {
    return firstValueFrom(
      this.http.post(`${this.base}/simulator/pumps/${pumpId}/flow`, { millilitresPerTick }),
    );
  }

  grade(pumpId: number, gradeCode: string): Promise<unknown> {
    return firstValueFrom(this.http.post(`${this.base}/simulator/pumps/${pumpId}/grade`, { gradeCode }));
  }

  suspendPump(pumpId: number): Promise<unknown> {
    return firstValueFrom(this.http.post(`${this.base}/simulator/pumps/${pumpId}/suspend`, {}));
  }

  resumePump(pumpId: number): Promise<unknown> {
    return firstValueFrom(this.http.post(`${this.base}/simulator/pumps/${pumpId}/resume`, {}));
  }

  // --- fault injection ------------------------------------------------------------------

  hostFault(body: { down?: boolean; latencyMs?: number; responseCode?: string }): Promise<FaultState> {
    return firstValueFrom(this.http.post<FaultState>(`${this.base}/faults/host`, body));
  }

  suspendSite(): Promise<FaultState> {
    return firstValueFrom(this.http.post<FaultState>(`${this.base}/faults/suspend`, {}));
  }

  resumeSite(): Promise<FaultState> {
    return firstValueFrom(this.http.post<FaultState>(`${this.base}/faults/resume`, {}));
  }

  corruptCrc(pumpId: number): Promise<FaultState> {
    return firstValueFrom(this.http.post<FaultState>(`${this.base}/faults/pumps/${pumpId}/corrupt-crc`, {}));
  }

  pumpPower(pumpId: number, state: 'on' | 'off'): Promise<FaultState> {
    return firstValueFrom(this.http.post<FaultState>(`${this.base}/faults/pumps/${pumpId}/power/${state}`, {}));
  }

  duplicate(transactionId: string): Promise<DuplicateResult> {
    return firstValueFrom(this.http.post<DuplicateResult>(`${this.base}/faults/duplicate/${transactionId}`, {}));
  }
}
