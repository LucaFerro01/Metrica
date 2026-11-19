import { beforeEach, describe, expect, it, vi } from 'vitest';
import { storage } from './storage';

// Questo file è esente dalla regola di lint su localStorage: verifica proprio il modulo che lo incapsula.

describe('storage con localStorage disponibile', () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  it('salva e rilegge un valore', () => {
    storage.set('chiave-a', 'valore');
    expect(storage.get('chiave-a')).toBe('valore');
    expect(window.localStorage.getItem('chiave-a')).toBe('valore');
  });

  it('restituisce null per una chiave assente', () => {
    expect(storage.get('assente')).toBeNull();
  });

  it('rimuove un valore', () => {
    storage.set('chiave-b', 'valore');
    storage.remove('chiave-b');
    expect(storage.get('chiave-b')).toBeNull();
    expect(window.localStorage.getItem('chiave-b')).toBeNull();
  });

  it('legge i valori scritti da altri (es. lo script inline o un altro tab)', () => {
    window.localStorage.setItem('chiave-c', 'esterno');
    expect(storage.get('chiave-c')).toBe('esterno');
  });
});

describe('storage con localStorage che lancia', () => {
  beforeEach(() => {
    window.localStorage.clear();
    // Es.: modalità privata di alcuni browser, quota esaurita, cookie bloccati.
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('negato', 'SecurityError');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('quota esaurita', 'QuotaExceededError');
    });
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      throw new DOMException('negato', 'SecurityError');
    });
  });

  it('non lancia e ripiega sulla memoria', () => {
    expect(() => storage.set('chiave-d', 'valore')).not.toThrow();
    expect(storage.get('chiave-d')).toBe('valore');
  });

  it('get di una chiave mai scritta restituisce null senza lanciare', () => {
    expect(storage.get('mai-scritta')).toBeNull();
  });

  it('remove non lancia e svuota anche la memoria', () => {
    storage.set('chiave-e', 'valore');
    expect(() => storage.remove('chiave-e')).not.toThrow();
    expect(storage.get('chiave-e')).toBeNull();
  });
});

describe('storage con accesso a localStorage che lancia (proprietà inaccessibile)', () => {
  it('non lancia e ripiega sulla memoria', () => {
    vi.spyOn(window, 'localStorage', 'get').mockImplementation(() => {
      throw new DOMException('negato', 'SecurityError');
    });
    expect(() => storage.set('chiave-f', 'valore')).not.toThrow();
    expect(storage.get('chiave-f')).toBe('valore');
    expect(() => storage.remove('chiave-f')).not.toThrow();
    expect(storage.get('chiave-f')).toBeNull();
  });
});

describe('storage senza localStorage (es. contesto privo di storage)', () => {
  it('ripiega sulla memoria', () => {
    vi.stubGlobal('localStorage', undefined);
    storage.set('chiave-g', 'valore');
    expect(storage.get('chiave-g')).toBe('valore');
    storage.remove('chiave-g');
    expect(storage.get('chiave-g')).toBeNull();
  });
});

describe('storage dopo un fallimento di scrittura', () => {
  it('quando lo storage torna disponibile una scrittura riuscita sostituisce il valore in memoria', () => {
    const setItem = vi.spyOn(Storage.prototype, 'setItem');
    setItem.mockImplementationOnce(() => {
      throw new DOMException('quota esaurita', 'QuotaExceededError');
    });
    storage.set('chiave-h', 'vecchio');
    storage.set('chiave-h', 'nuovo'); // ora la scrittura riesce (mockImplementationOnce esaurito)
    window.localStorage.removeItem('chiave-h');
    // Il valore in memoria non deve resuscitare quello vecchio dopo una scrittura riuscita.
    expect(storage.get('chiave-h')).toBeNull();
  });
});
