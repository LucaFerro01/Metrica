import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { navItems } from './navItems';

/**
 * Navigazione in basso: solo sotto 768 px (`md:hidden`). Fissa in fondo allo schermo, con il margine
 * sicuro inferiore (barra di gesti dell'iPhone) e quelli laterali.
 */
export function BottomNav() {
  const { t } = useTranslation();
  return (
    <nav
      aria-label={t('nav.bottomLabel')}
      className="fixed inset-x-0 bottom-0 z-10 border-t border-border bg-surface pr-[env(safe-area-inset-right)] pb-[env(safe-area-inset-bottom)] pl-[env(safe-area-inset-left)] md:hidden"
    >
      <ul className="grid grid-cols-2">
        {navItems.map(({ to, labelKey, icon: Icon }) => (
          <li key={to} className="min-w-0">
            <Link
              to={to}
              className="flex min-h-11 flex-col items-center justify-center gap-0.5 px-2 py-2 text-xs font-medium text-muted data-[status=active]:text-accent"
            >
              <Icon aria-hidden="true" className="size-5 shrink-0" />
              <span className="max-w-full truncate">{t(labelKey)}</span>
            </Link>
          </li>
        ))}
      </ul>
    </nav>
  );
}
