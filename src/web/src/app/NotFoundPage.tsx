import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { PageHeader } from '@/shared/ui';

export function NotFoundPage() {
  const { t } = useTranslation();
  return (
    <section>
      <PageHeader title={t('notFound.title')} description={t('notFound.description')} />
      <Link to="/activities" className="inline-flex min-h-11 items-center font-medium text-accent underline">
        {t('notFound.back')}
      </Link>
    </section>
  );
}
