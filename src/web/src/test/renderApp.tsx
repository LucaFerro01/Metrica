import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { render } from '@testing-library/react';
import { AppProviders } from '@/app/providers';
import { createAppRouter } from '@/app/router';
import { i18n, initI18n } from '@/shared/i18n';
import { initTheme } from '@/shared/lib/theme';

/**
 * Monta l'applicazione intera con un router in memoria, in italiano e con il tema inizializzato
 * (come in `main.tsx`). Restituisce anche la funzione di chiusura del tema, da chiamare a fine test.
 */
export async function renderApp(path = '/') {
  await initI18n();
  await i18n.changeLanguage('it');
  const disposeTheme = initTheme();

  const router = createAppRouter(createMemoryHistory({ initialEntries: [path] }));
  await router.load();
  const view = render(
    <AppProviders>
      <RouterProvider router={router} />
    </AppProviders>,
  );
  return { ...view, router, disposeTheme };
}
