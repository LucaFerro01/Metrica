import { isSport, type Sport } from './sports';

/** Parametri di ricerca di `/activities` (e, per il link di ritorno, di `/activities/$activityId`). */
export interface ActivitiesSearch {
  /** Assente = tutti gli sport. */
  sport?: Sport | undefined;
  /** Assente = pagina 1 (si omette per tenere pulito l'indirizzo). */
  page?: number | undefined;
}

/** Oltre questo valore la pagina non può esistere (il server usa interi a 32 bit): si torna alla prima. */
export const MAX_PAGE = 1_000_000;

/**
 * Valida i parametri di ricerca dell'indirizzo. Un valore non valido NON è mai un errore: si scarta e vale
 * il predefinito (tutti gli sport, pagina 1). `page=1` è il predefinito e non si riporta.
 *
 * Restituisce SEMPRE entrambe le chiavi, anche `undefined`: TanStack Router fonde il risultato sopra i
 * parametri grezzi ereditati dal genitore, quindi una chiave mancante lascerebbe passare il valore non valido
 * (es. `?page=0`); una chiave `undefined` lo sovrascrive.
 */
export function parseActivitiesSearch(raw: Record<string, unknown>): ActivitiesSearch {
  const sport = isSport(raw.sport) ? raw.sport : undefined;
  const page = typeof raw.page === 'string' && /^\d{1,7}$/.test(raw.page) ? Number(raw.page) : raw.page;
  const validPage = typeof page === 'number' && Number.isInteger(page) && page >= 2 && page <= MAX_PAGE;
  return { sport, page: validPage ? page : undefined };
}
