import { useTranslation } from 'react-i18next';
import { isSport } from '../data/sports';

/** Etichetta tradotta di uno sport; un valore sconosciuto (sport nuovo del server) si mostra come «Altro». */
export function useSportLabel(): (sport: string) => string {
  const { t } = useTranslation();
  return (sport) => (isSport(sport) ? t(`activities.sport.${sport}`) : t('activities.sport.other'));
}
