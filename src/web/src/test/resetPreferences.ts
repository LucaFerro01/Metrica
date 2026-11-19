import { LANGUAGE_STORAGE_KEY } from '@/shared/i18n';
import { storage } from '@/shared/lib/storage';
import { THEME_STORAGE_KEY } from '@/shared/lib/theme';

/** Cancella le preferenze persistenti (tema e lingua) tra un test e l'altro, passando da `storage`. */
export function resetPersistedPreferences(): void {
  storage.remove(THEME_STORAGE_KEY);
  storage.remove(LANGUAGE_STORAGE_KEY);
}
