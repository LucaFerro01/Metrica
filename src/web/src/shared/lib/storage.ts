// Unico punto del codice che tocca `localStorage` (DEC-015, REQ-038): in un guscio nativo, in modalità
// privata o con i cookie bloccati lo storage può mancare o lanciare. Qui ogni accesso è protetto da
// try/catch e, se lo storage non funziona, si ripiega su una mappa in memoria (valida per la sessione).
// Tutto il resto del codice usa questo modulo: la regola di lint in `eslint.config.js` vieta
// `localStorage`, `sessionStorage` e `window.open` ovunque tranne qui.
//
// Unica eccezione documentata: lo script inline in `index.html` che applica il tema prima del primo
// disegno legge la stessa chiave (un test ne verifica la coincidenza con `THEME_STORAGE_KEY`).

const memory = new Map<string, string>();

function backend(): Storage | null {
  try {
    return globalThis.localStorage ?? null;
  } catch {
    return null;
  }
}

export const storage = {
  /** Legge un valore; `null` se assente o se lo storage non è utilizzabile. */
  get(key: string): string | null {
    try {
      const value = backend()?.getItem(key);
      if (value !== null && value !== undefined) return value;
    } catch {
      // lo storage lancia: si usa la memoria
    }
    return memory.get(key) ?? null;
  },

  /** Salva un valore; se lo storage non funziona lo tiene solo in memoria. Non lancia mai. */
  set(key: string, value: string): void {
    try {
      const store = backend();
      if (store) {
        store.setItem(key, value);
        memory.delete(key); // la copia persistente è l'unica fonte di verità
        return;
      }
    } catch {
      // quota esaurita o accesso negato: ripiego sulla memoria
    }
    memory.set(key, value);
  },

  /** Rimuove un valore dallo storage e dalla memoria. Non lancia mai. */
  remove(key: string): void {
    try {
      backend()?.removeItem(key);
    } catch {
      // niente da fare: la copia in memoria si toglie comunque
    }
    memory.delete(key);
  },
};
