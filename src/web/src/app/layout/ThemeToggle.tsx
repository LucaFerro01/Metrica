import { Monitor, Moon, Sun } from 'lucide-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { THEME_PREFERENCES, type ThemePreference } from '@/shared/lib/theme';
import { useTheme } from '@/shared/lib/useTheme';
import { SegmentedControl, type SegmentedOption } from '@/shared/ui';

const icons: Record<ThemePreference, ReactNode> = {
  system: <Monitor aria-hidden="true" className="size-5" />,
  light: <Sun aria-hidden="true" className="size-5" />,
  dark: <Moon aria-hidden="true" className="size-5" />,
};

/** Interruttore a tre posizioni: Sistema, Chiaro, Scuro. */
export function ThemeToggle() {
  const { t } = useTranslation();
  const { preference, setPreference } = useTheme();
  const options: SegmentedOption<ThemePreference>[] = THEME_PREFERENCES.map((value) => ({
    value,
    label: t(`theme.${value}`),
    content: icons[value],
  }));
  return <SegmentedControl label={t('theme.label')} value={preference} options={options} onChange={setPreference} />;
}
