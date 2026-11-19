// Formattazione di distanze, durate, dislivelli, date e metriche nella lingua corrente (`Intl`).
// Tutte le funzioni accettano `null`/`undefined` e restituiscono `null` (la UI mostra «non disponibile»
// tradotto); anche i valori non validi (NaN, infiniti, e negativi dove non hanno senso) danno `null`.
// Il fuso orario è quello del browser.

/** Spazio non separabile: il numero non va mai a capo staccato dalla sua unità. */
const NBSP = ' ';

export type MetricUnit = 'bpm' | 'W' | 'rpm';
type DateInput = string | number | Date;

function isFiniteNumber(value: number | null | undefined): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

/** `Math.round` senza lo zero negativo (-0 verrebbe stampato come "-0"). */
function roundInt(value: number): number {
  const rounded = Math.round(value);
  return Object.is(rounded, -0) ? 0 : rounded;
}

function integer(value: number, locale: string): string {
  return new Intl.NumberFormat(locale, { maximumFractionDigits: 0 }).format(roundInt(value));
}

/**
 * Distanza da metri: da 1 km in su in chilometri con 2 decimali ("12,35 km"); sotto 1 km in metri
 * interi ("850 m"), perché per nuoto e brevi tratti "0,85 km" è meno leggibile. Un valore che
 * arrotonda a 1000 m diventa "1,00 km" (mai "1000 m").
 */
export function formatDistance(meters: number | null | undefined, locale: string): string | null {
  if (!isFiniteNumber(meters) || meters < 0) return null;
  if (Math.round(meters) < 1000) return `${integer(meters, locale)}${NBSP}m`;
  const km = new Intl.NumberFormat(locale, { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(
    meters / 1000,
  );
  return `${km}${NBSP}km`;
}

/** Durata da secondi: `h:mm:ss` da un'ora in su, altrimenti `m:ss`. Le ore non si troncano mai. */
export function formatDuration(seconds: number | null | undefined): string | null {
  if (!isFiniteNumber(seconds) || seconds < 0) return null;
  const total = Math.round(seconds);
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const secs = total % 60;
  const ss = String(secs).padStart(2, '0');
  if (hours > 0) return `${hours}:${String(minutes).padStart(2, '0')}:${ss}`;
  return `${minutes}:${ss}`;
}

/** Dislivello in metri interi ("1.234 m" / "1,234 m"). */
export function formatElevation(meters: number | null | undefined, locale: string): string | null {
  if (!isFiniteNumber(meters)) return null;
  return `${integer(meters, locale)}${NBSP}m`;
}

/** Intero con unità: frequenza cardiaca (bpm), potenza (W), cadenza (rpm). */
export function formatMetric(value: number | null | undefined, unit: MetricUnit, locale: string): string | null {
  if (!isFiniteNumber(value)) return null;
  return `${integer(value, locale)}${NBSP}${unit}`;
}

function toDate(value: DateInput | null | undefined): Date | null {
  if (value === null || value === undefined) return null;
  const date = value instanceof Date ? value : new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

/** Data e ora brevi nel fuso del browser (es. "19 nov 2026, 14:30"). */
export function formatDateTime(value: DateInput | null | undefined, locale: string): string | null {
  const date = toDate(value);
  if (!date) return null;
  return new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' }).format(date);
}

/** Solo la data (es. "19 nov 2026"), nel fuso del browser. */
export function formatDate(value: DateInput | null | undefined, locale: string): string | null {
  const date = toDate(value);
  if (!date) return null;
  return new Intl.DateTimeFormat(locale, { dateStyle: 'medium' }).format(date);
}
