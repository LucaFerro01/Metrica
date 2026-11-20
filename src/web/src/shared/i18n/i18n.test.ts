import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { storage } from '@/shared/lib/storage';
import { resetPersistedPreferences } from '@/test/resetPreferences';
import { detectInitialLanguage, i18n, initI18n, LANGUAGE_STORAGE_KEY, setLanguage } from './index';

function setNavigatorLanguage(language: string | undefined) {
  vi.spyOn(window.navigator, 'language', 'get').mockReturnValue(language as string);
}

beforeEach(() => {
  resetPersistedPreferences();
  setNavigatorLanguage('en-US');
});

describe('detectInitialLanguage', () => {
  it('usa la scelta salvata, anche se il browser è in un\'altra lingua', () => {
    storage.set(LANGUAGE_STORAGE_KEY, 'it');
    setNavigatorLanguage('en-GB');
    expect(detectInitialLanguage()).toBe('it');

    storage.set(LANGUAGE_STORAGE_KEY, 'en');
    setNavigatorLanguage('it-IT');
    expect(detectInitialLanguage()).toBe('en');
  });

  it('senza scelta salvata, un browser in italiano dà italiano', () => {
    setNavigatorLanguage('it-IT');
    expect(detectInitialLanguage()).toBe('it');
    setNavigatorLanguage('it');
    expect(detectInitialLanguage()).toBe('it');
    setNavigatorLanguage('IT-ch');
    expect(detectInitialLanguage()).toBe('it');
  });

  it('senza scelta salvata, un browser non italiano dà inglese', () => {
    setNavigatorLanguage('en-US');
    expect(detectInitialLanguage()).toBe('en');
    setNavigatorLanguage('fr-FR');
    expect(detectInitialLanguage()).toBe('en');
  });

  it('ignora un valore salvato non supportato', () => {
    storage.set(LANGUAGE_STORAGE_KEY, 'fr');
    setNavigatorLanguage('it-IT');
    expect(detectInitialLanguage()).toBe('it');
  });

  it('con navigator.language assente ripiega sull\'inglese', () => {
    setNavigatorLanguage(undefined);
    expect(detectInitialLanguage()).toBe('en');
  });
});

describe('initI18n e setLanguage', () => {
  afterEach(async () => {
    await i18n.changeLanguage('it');
  });

  it('parte nella lingua rilevata e aggiorna l\'attributo lang del documento', async () => {
    setNavigatorLanguage('en-US');
    await initI18n();
    expect(i18n.language).toBe('en');
    expect(document.documentElement.lang).toBe('en');
  });

  it('setLanguage cambia i testi, salva la scelta e aggiorna lang', async () => {
    await initI18n();
    await setLanguage('it');
    expect(i18n.t('nav.activities')).toBe('Attività');
    expect(document.documentElement.lang).toBe('it');
    expect(storage.get(LANGUAGE_STORAGE_KEY)).toBe('it');

    await setLanguage('en');
    expect(i18n.t('nav.activities')).toBe('Activities');
    expect(document.documentElement.lang).toBe('en');
    expect(storage.get(LANGUAGE_STORAGE_KEY)).toBe('en');
  });

  it('una lingua non supportata ripiega sull\'italiano (fallbackLng)', async () => {
    await initI18n();
    await i18n.changeLanguage('fr');
    expect(i18n.t('nav.activities')).toBe('Attività');
  });

  it('initI18n è idempotente', async () => {
    const first = await initI18n();
    const second = await initI18n();
    expect(first).toBe(second);
  });
});
