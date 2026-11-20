import { Outlet } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { BottomNav } from './BottomNav';
import { LanguageSwitch } from './LanguageSwitch';
import { SideNav } from './SideNav';
import { ThemeToggle } from './ThemeToggle';

/**
 * Guscio dell'applicazione con due modalità di layout (DEC-015, REQ-037):
 * - sotto 768 px: intestazione in alto, contenuto a colonna e navigazione in basso;
 * - da 768 px: intestazione, barra laterale a sinistra e contenuto.
 * Altezza con `dvh` (la barra degli indirizzi dei telefoni cambia l'altezza visibile), margini sicuri
 * con `env(safe-area-inset-*)` (richiedono `viewport-fit=cover` in index.html) e `min-w-0` sul contenuto
 * perché nulla allarghi la pagina oltre la viewport.
 */
export function AppShell() {
  const { t } = useTranslation();
  return (
    <div className="flex min-h-dvh flex-col bg-background pr-[env(safe-area-inset-right)] pl-[env(safe-area-inset-left)] text-foreground">
      <header className="sticky top-0 z-10 border-b border-border bg-surface pt-[env(safe-area-inset-top)]">
        <div className="flex min-h-14 items-center justify-between gap-2 px-4 md:px-6">
          <span className="min-w-0 truncate text-lg font-semibold tracking-tight">{t('app.name')}</span>
          <div className="flex shrink-0 items-center gap-2">
            <ThemeToggle />
            <LanguageSwitch />
          </div>
        </div>
      </header>
      <div className="flex min-h-0 flex-1">
        <SideNav />
        <main className="min-w-0 flex-1 px-4 pt-6 pb-[calc(5rem+env(safe-area-inset-bottom))] md:px-8 md:pt-8 md:pb-8">
          <Outlet />
        </main>
      </div>
      <BottomNav />
    </div>
  );
}
