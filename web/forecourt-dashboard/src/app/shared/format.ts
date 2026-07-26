/** Formatting shared across the console. Money is minor units everywhere on the wire. */

/** Formats minor units as a decimal amount, without a currency symbol. */
export function money(minor: number): string {
  return (minor / 100).toFixed(2);
}

/** Formats millilitres as litres to three places, the resolution the flow meter reports. */
export function litres(millilitres: number): string {
  return (millilitres / 1000).toFixed(3);
}

/** Formats an elapsed span as `m:ss`, for the pump tile's session timer. */
export function elapsed(fromIso: string | null, now: number): string {
  if (!fromIso) {
    return '—';
  }

  const seconds = Math.max(0, Math.floor((now - Date.parse(fromIso)) / 1000));
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
}

/** Seconds remaining until a deadline, floored at zero. Drives the OPT countdown. */
export function countdown(deadlineIso: string | null, now: number): number | null {
  if (!deadlineIso) {
    return null;
  }

  return Math.max(0, Math.ceil((Date.parse(deadlineIso) - now) / 1000));
}

/** Breaks a hex string into space-separated byte pairs so a trace stays readable. */
export function hexBytes(hex: string): string {
  return hex.replace(/([0-9A-Fa-f]{2})(?=[0-9A-Fa-f])/g, '$1 ');
}
