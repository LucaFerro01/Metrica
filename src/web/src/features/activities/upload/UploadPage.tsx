import { useTranslation } from 'react-i18next';
import { PageHeader } from '@/shared/ui';

/** Segnaposto: la pagina di caricamento vera (selettore di file, avanzamento, esiti) arriva in T17. */
export function UploadPage() {
  const { t } = useTranslation();
  return (
    <section>
      <PageHeader title={t('activities.upload.title')} description={t('activities.upload.placeholder')} />
    </section>
  );
}
