import i18next from 'i18next';
import { initReactI18next } from 'react-i18next';
import { storage } from '@/shared/lib/storage';
import en from './locales/en.json';
import it from './locales/it.json';

// Lingue dell'interfaccia (DEC-016): italiano predefinito, inglese se il browser non è italiano.
// Tutti i testi stanno in `locales/*.json`; un test verifica che abbiano le stesse chiavi e gli stessi segnaposto.

export const SUPPORTED_LANGUAGES = ['it', 'en'] as const;
export type Language = (typeof SUPPORTED_LANGUAGES)[number];

export const DEFAULT_LANGUAGE: Language = 'it';

/** Chiave di storage della lingua scelta dall'utente. */
export const LANGUAGE_STORAGE_KEY = 'metrica.language';

export const i18n = i18next.createInstance();

export function isLanguage(value: string | null | undefined): value is Language {
  return (SUPPORTED_LANGUAGES as readonly string[]).includes(value ?? '');
}

/** Scelta salvata; altrimenti `navigator.language` che inizia per "it" → it, tutto il resto → en. */
export function detectInitialLanguage(): Language {
  const stored = storage.get(LANGUAGE_STORAGE_KEY);
  if (isLanguage(stored)) return stored;
  const browser = typeof navigator === 'undefined' ? '' : (navigator.language ?? '');
  return browser.toLowerCase().startsWith('it') ? 'it' : 'en';
}

function syncDocumentLanguage(): void {
  document.documentElement.lang = i18n.resolvedLanguage ?? DEFAULT_LANGUAGE;
}

let initialization: Promise<typeof i18n> | null = null;

/** Inizializza i18next una sola volta (idempotente) e tiene allineato `<html lang>`. */
export function initI18n(): Promise<typeof i18n> {
  initialization ??= i18n
    .use(initReactI18next)
    .init({
      resources: { it: { translation: it }, en: { translation: en } },
      lng: detectInitialLanguage(),
      fallbackLng: DEFAULT_LANGUAGE,
      supportedLngs: [...SUPPORTED_LANGUAGES],
      interpolation: { escapeValue: false }, // React già neutralizza l'output
      initAsync: false, // risorse incluse nel bundle: nessun caricamento da attendere
    })
    .then(() => {
      i18n.on('languageChanged', syncDocumentLanguage);
      syncDocumentLanguage();
      return i18n;
    });
  return initialization;
}

/** Cambia la lingua dell'interfaccia e ricorda la scelta. */
export async function setLanguage(language: Language): Promise<void> {
  await i18n.changeLanguage(language);
  storage.set(LANGUAGE_STORAGE_KEY, language);
}
