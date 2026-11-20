/** Unisce nomi di classe ignorando i valori falsi (niente dipendenze esterne). */
export function cn(...parts: (string | false | null | undefined)[]): string {
  return parts.filter(Boolean).join(' ');
}
