import { useTranslation } from 'react-i18next';
import { SegmentedControl } from '@/shared/ui';
import { SPORTS, type Sport } from '../data/sports';

export type SportFilterValue = 'all' | Sport;

interface SportFilterProps {
  value: SportFilterValue;
  onChange: (value: SportFilterValue) => void;
}

/** Filtro per sport: Tutti, Corsa, Bici, Nuoto, Altro. Pulsanti da 44 px con `aria-pressed` sul selezionato. */
export function SportFilter({ value, onChange }: SportFilterProps) {
  const { t } = useTranslation();
  const values: readonly SportFilterValue[] = ['all', ...SPORTS];
  const options = values.map((option) => {
    const label = t(`activities.sport.${option}`);
    return { value: option, label, content: label };
  });
  return <SegmentedControl label={t('activities.list.filterLabel')} value={value} options={options} onChange={onChange} />;
}
