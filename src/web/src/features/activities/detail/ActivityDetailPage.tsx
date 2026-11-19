import { useTranslation } from 'react-i18next';
import { PageHeader } from '@/shared/ui';

interface ActivityDetailPageProps {
  activityId: string;
}

/** Segnaposto: il dettaglio vero (riepilogo e giri) arriva in T18. */
export function ActivityDetailPage({ activityId }: ActivityDetailPageProps) {
  const { t } = useTranslation();
  return (
    <section>
      <PageHeader
        title={t('activities.detail.title')}
        description={t('activities.detail.placeholder', { id: activityId })}
      />
    </section>
  );
}
