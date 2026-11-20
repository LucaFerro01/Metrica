import type { components } from '@/shared/api/activities';

/** Sport ammessi dall'API (tipo generato): sono anche i valori ammessi del filtro `sport`. */
export type Sport = components['schemas']['Sport'];

/** Ordine di visualizzazione nel filtro. */
export const SPORTS = ['run', 'ride', 'swim', 'other'] as const satisfies readonly Sport[];

// Se l'API aggiungesse uno sport, questo controllo di tipo smette di compilare e ricorda di aggiornare SPORTS.
type MissingSports = Exclude<Sport, (typeof SPORTS)[number]>;
const everySportListed: MissingSports extends never ? true : never = true;
void everySportListed;

export function isSport(value: unknown): value is Sport {
  return typeof value === 'string' && (SPORTS as readonly string[]).includes(value);
}
