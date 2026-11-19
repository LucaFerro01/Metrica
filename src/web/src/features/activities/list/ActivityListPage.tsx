import { useTranslation } from 'react-i18next';
import { PageHeader } from '@/shared/ui';

/** Segnaposto: l'elenco vero (schede/tabella, filtro, paginazione, polling) arriva in T18. */
export function ActivityListPage() {
  const { t } = useTranslation();
  return (
    <section>
      <PageHeader title={t('activities.list.title')} description={t('activities.list.placeholder')} />
    </section>
  );
}
