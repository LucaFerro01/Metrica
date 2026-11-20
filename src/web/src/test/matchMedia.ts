import { vi } from 'vitest';

type Listener = (event: MediaQueryListEvent) => void;

export interface FakeMediaQuery {
  /** Simula il cambio di preferenza del sistema (notifica i listener registrati). */
  setMatches: (matches: boolean) => void;
  /** Numero di listener `change` attualmente registrati. */
  listenerCount: () => number;
}

/**
 * jsdom non implementa `matchMedia` e non valuta le media query. Questo finto controllabile
 * simula `(prefers-color-scheme: dark)`; ogni altra query risulta falsa.
 */
export function installFakeMatchMedia(initiallyDark: boolean): FakeMediaQuery {
  let dark = initiallyDark;
  const listeners = new Set<Listener>();

  const matchMedia = (query: string): MediaQueryList => {
    const isDarkQuery = query.includes('prefers-color-scheme: dark');
    return {
      get matches() {
        return isDarkQuery ? dark : false;
      },
      media: query,
      onchange: null,
      addEventListener: (type: string, listener: Listener) => {
        if (type === 'change' && isDarkQuery) listeners.add(listener);
      },
      removeEventListener: (type: string, listener: Listener) => {
        if (type === 'change' && isDarkQuery) listeners.delete(listener);
      },
      addListener: () => undefined,
      removeListener: () => undefined,
      dispatchEvent: () => true,
    } as unknown as MediaQueryList;
  };

  vi.stubGlobal('matchMedia', matchMedia);

  return {
    setMatches: (matches) => {
      dark = matches;
      for (const listener of [...listeners]) {
        listener({ matches, media: '(prefers-color-scheme: dark)' } as MediaQueryListEvent);
      }
    },
    listenerCount: () => listeners.size,
  };
}
