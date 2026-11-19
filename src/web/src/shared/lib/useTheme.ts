import { useSyncExternalStore } from 'react';
import { getThemePreference, setThemePreference, subscribeThemePreference } from './theme';

/** Preferenza di tema corrente (sistema/chiaro/scuro) e funzione per cambiarla. */
export function useTheme() {
  const preference = useSyncExternalStore(subscribeThemePreference, getThemePreference, getThemePreference);
  return { preference, setPreference: setThemePreference };
}
