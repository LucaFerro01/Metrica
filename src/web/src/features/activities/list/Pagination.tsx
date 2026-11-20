import { ChevronLeft, ChevronRight } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/shared/ui';

interface PaginationProps {
  page: number;
  pages: number;
  onPageChange: (page: number) => void;
}

/** «Precedente», «Pagina X di Y», «Successiva»: i pulsanti sono disabilitati ai bordi (prima e ultima pagina). */
export function Pagination({ page, pages, onPageChange }: PaginationProps) {
  const { t } = useTranslation();
  return (
    <nav aria-label={t('activities.list.pagination.label')} className="mt-6 flex items-center justify-between gap-3">
      <Button variant="secondary" disabled={page <= 1} onClick={() => onPageChange(page - 1)}>
        <ChevronLeft aria-hidden="true" className="size-4 shrink-0" />
        {t('activities.list.pagination.previous')}
      </Button>
      <p className="min-w-0 text-center text-sm text-muted" aria-live="polite">
        {t('activities.list.pagination.pageOf', { page, pages })}
      </p>
      <Button variant="secondary" disabled={page >= pages} onClick={() => onPageChange(page + 1)}>
        {t('activities.list.pagination.next')}
        <ChevronRight aria-hidden="true" className="size-4 shrink-0" />
      </Button>
    </nav>
  );
}
