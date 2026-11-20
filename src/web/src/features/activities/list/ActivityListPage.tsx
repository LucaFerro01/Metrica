import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Link, useNavigate } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { PageHeader, cn } from '@/shared/ui';
import { activityKeys, fetchActivityList, PAGE_SIZE, type ActivityListItem } from '../data/activitiesApi';
import type { ActivitiesSearch } from '../data/activitiesSearch';
import type { Sport } from '../data/sports';
import { LoadingStatus } from '../display/LoadingStatus';
import { RequestError } from '../display/RequestError';
import { ActivityCards, ActivityTable } from './ActivityRows';
import { Pagination } from './Pagination';
import { SportFilter, type SportFilterValue } from './SportFilter';
import { usePendingTracking, type TrackedUpload } from './usePendingTracking';

const linkClass = 'inline-flex min-h-11 items-center font-medium text-accent underline';

/** Numero di pagine per `total` attività (almeno 1). */
function pageCount(total: number): number {
  return Math.max(1, Math.ceil(total / PAGE_SIZE));
}

/**
 * Elenco delle attività (REQ-027, REQ-037): schede sotto 768 px e tabella da 768 px, filtro per sport,
 * paginazione da 20, righe «in elaborazione» in cima agli upload accettati e non ancora comparsi.
 *
 * Filtro e pagina vivono nell'indirizzo (`?sport=run&page=2`, validati dal router: un valore non valido è
 * ignorato, mai un errore). Le righe «in elaborazione» si mostrano SOLO a pagina 1 e senza filtro per sport
 * (altrimenti sembrerebbero far parte di un'altra selezione); il rilevamento invece prosegue comunque, così
 * un upload già importato non resta in sospeso.
 */
export function ActivityListPage({ sport, page = 1 }: ActivitiesSearch) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { uploads: tracked, announcement } = usePendingTracking();

  const query = useQuery({
    queryKey: activityKeys.list({ sport, page }),
    queryFn: ({ signal }) => fetchActivityList({ sport, page }, signal),
    // Cambiando filtro o pagina si resta sull'elenco precedente (attenuato) finché arriva il nuovo.
    placeholderData: keepPreviousData,
    // Nessun tentativo automatico: c'è «Riprova», e l'errore si vede subito.
    retry: false,
  });

  const data = query.data;
  const pendingRows = sport === undefined && page === 1 ? tracked : [];

  function goTo(next: ActivitiesSearch) {
    void navigate({ to: '/activities', search: next });
  }

  function changeSport(value: SportFilterValue) {
    // Un altro filtro riparte dalla prima pagina.
    goTo(value === 'all' ? {} : { sport: value });
  }

  function changePage(next: number) {
    goTo({ ...(sport ? { sport } : {}), ...(next > 1 ? { page: next } : {}) });
  }

  return (
    <section className="min-w-0">
      <PageHeader title={t('activities.list.title')} />

      {/* Regione live SEMPRE presente (un `role=status` inserito dopo il caricamento non viene annunciato in modo
          affidabile): annuncia l'esito delle righe «in elaborazione». Invisibile a schermo. */}
      <div className="sr-only" aria-live="polite" aria-atomic="true" data-testid="pending-announcer">
        {announcement === 'imported' ? t('activities.list.pending.announceImported') : null}
        {announcement === 'timedOut' ? t('activities.list.pending.announceTimedOut') : null}
      </div>

      <div className="mb-4 flex min-w-0 flex-wrap items-center gap-3">
        <SportFilter value={sport ?? 'all'} onChange={changeSport} />
      </div>

      {query.isError ? (
        <div className="mb-4">
          <RequestError error={query.error} onRetry={() => void query.refetch()} retrying={query.isFetching} />
        </div>
      ) : null}

      {query.isPending ? <LoadingStatus label={t('activities.list.loading')} /> : null}

      {data ? (
        <ListBody
          items={data.items}
          total={data.total}
          sport={sport}
          page={page}
          pendingRows={pendingRows}
          busy={query.isPlaceholderData}
          onPageChange={changePage}
        />
      ) : null}
    </section>
  );
}

interface ListBodyProps {
  items: readonly ActivityListItem[];
  total: number;
  sport: Sport | undefined;
  page: number;
  pendingRows: readonly TrackedUpload[];
  busy: boolean;
  onPageChange: (page: number) => void;
}

function ListBody({ items, total, sport, page, pendingRows, busy, onPageChange }: ListBodyProps) {
  const { t } = useTranslation();
  const pages = pageCount(total);
  const search: ActivitiesSearch = { ...(sport ? { sport } : {}), ...(page > 1 ? { page } : {}) };

  if (items.length === 0 && pendingRows.length === 0) {
    // Pagina oltre l'ultima (indirizzo scritto a mano o attività cancellate): si spiega e si offre l'ultima pagina.
    if (total > 0 && page > pages) {
      return (
        <div className="flex flex-col items-start gap-2">
          <p>{t('activities.list.pageMissing')}</p>
          <Link
            to="/activities"
            search={{ ...(sport ? { sport } : {}), ...(pages > 1 ? { page: pages } : {}) }}
            className={linkClass}
          >
            {t('activities.list.lastPage')}
          </Link>
        </div>
      );
    }
    if (sport) return <p className="py-4 text-muted">{t('activities.list.emptyFiltered')}</p>;
    return (
      <div className="flex flex-col items-start gap-2 py-4">
        <p>{t('activities.list.empty')}</p>
        <Link to="/upload" className={linkClass}>
          {t('activities.list.emptyAction')}
        </Link>
      </div>
    );
  }

  return (
    <div aria-busy={busy} className={cn('min-w-0 transition-opacity', busy && 'opacity-60')}>
      <ActivityCards items={items} pending={pendingRows} search={search} />
      <ActivityTable items={items} pending={pendingRows} search={search} />
      {total > 0 ? <Pagination page={page} pages={pages} onPageChange={onPageChange} /> : null}
    </div>
  );
}
