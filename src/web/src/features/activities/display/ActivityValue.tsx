import { useTranslation } from 'react-i18next';

/**
 * Un valore già formattato oppure, se manca (`null`), «non disponibile» tradotto in tono attenuato.
 * Mai «0» o vuoto al posto di un dato assente.
 */
export function ActivityValue({ value }: { value: string | null | undefined }) {
  const { t } = useTranslation();
  if (value === null || value === undefined || value === '') {
    return <span className="font-normal text-muted">{t('common.unavailable')}</span>;
  }
  return <>{value}</>;
}
