import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { navItems } from './navItems';

/** Barra laterale: solo da 768 px (`hidden md:flex`). */
export function SideNav() {
  const { t } = useTranslation();
  return (
    <nav
      aria-label={t('nav.sideLabel')}
      className="hidden w-56 shrink-0 flex-col gap-1 border-r border-border bg-surface p-3 md:flex"
    >
      {navItems.map(({ to, labelKey, icon: Icon }) => (
        <Link
          key={to}
          to={to}
          className="flex min-h-11 items-center gap-3 rounded-lg px-3 text-sm font-medium text-muted hover:text-foreground data-[status=active]:bg-background data-[status=active]:text-accent"
        >
          <Icon aria-hidden="true" className="size-5 shrink-0" />
          <span className="truncate">{t(labelKey)}</span>
        </Link>
      ))}
    </nav>
  );
}
