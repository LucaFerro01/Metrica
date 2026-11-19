import { useTranslation } from 'react-i18next';
import { setLanguage, SUPPORTED_LANGUAGES, type Language } from '@/shared/i18n';
import { SegmentedControl, type SegmentedOption } from '@/shared/ui';

/** Selettore di lingua (italiano/inglese), accanto all'interruttore del tema. */
export function LanguageSwitch() {
  const { t, i18n } = useTranslation();
  const current: Language = i18n.resolvedLanguage === 'en' ? 'en' : 'it';
  const options: SegmentedOption<Language>[] = SUPPORTED_LANGUAGES.map((value) => ({
    value,
    label: t(`language.${value}`),
    content: t(`language.short.${value}`),
  }));
  return (
    <SegmentedControl
      label={t('language.label')}
      value={current}
      options={options}
      onChange={(language) => void setLanguage(language)}
    />
  );
}
