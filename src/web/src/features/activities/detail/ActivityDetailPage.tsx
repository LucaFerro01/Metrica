import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { ArrowLeft } from 'lucide-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useFormatters } from '@/shared/i18n/useFormatters';
import { PageHeader } from '@/shared/ui';
import {
  ActivitiesRequestError,
  activityKeys,
  fetchActivityDetail,
  isActivityId,
  type ActivityDetail,
} from '../data/activitiesApi';
import type { ActivitiesSearch } from '../data/activitiesSearch';
import { ActivityValue } from '../display/ActivityValue';
import { LoadingStatus } from '../display/LoadingStatus';
import { RequestError } from '../display/RequestError';
import { useSportLabel } from '../display/useSportLabel';
import { LapsSection } from './LapsSection';

const linkClass = 'inline-flex min-h-11 items-center gap-2 font-medium text-accent underline';

interface ActivityDetailPageProps {
  activityId: string;
  /** Filtro e pagina dell'elenco da cui si arriva: il link «Torna all'elenco» li conserva. */
  backSearch?: ActivitiesSearch;
}

/**
 * Dettaglio di un'attività (REQ-028): riepilogo con numeri grandi e giri. «Non disponibile» per i campi
 * assenti. Niente grafici, mappe né stream (fuori ambito): si chiedono al server solo 2 punti (`maxPoints=2`).
 * Un id che non è un UUID o che il server non conosce (404, identico per un'attività altrui) dà la stessa
 * pagina «Attività non trovata».
 */
export function ActivityDetailPage({ activityId, backSearch = {} }: ActivityDetailPageProps) {
  const { t } = useTranslation();
  const validId = isActivityId(activityId);

  const query = useQuery({
    queryKey: activityKeys.detail(activityId),
    queryFn: ({ signal }) => fetchActivityDetail(activityId, signal),
    // Un id che non ha la forma di un UUID non può esistere: nessuna richiesta.
    enabled: validId,
    retry: false,
  });

  const back = (
    <Link to="/activities" search={backSearch} className={linkClass}>
      <ArrowLeft aria-hidden="true" className="size-4 shrink-0" />
      {t('activities.detail.back')}
    </Link>
  );

  if (!validId || (query.isError && isNotFound(query.error))) {
    return (
      <section className="min-w-0">
        <PageHeader title={t('activities.detail.notFound.title')} description={t('activities.detail.notFound.description')} />
        {back}
      </section>
    );
  }

  if (query.isError) {
    return (
      <section className="min-w-0">
        <PageHeader title={t('activities.detail.title')} />
        <div className="mb-4">
          <RequestError error={query.error} onRetry={() => void query.refetch()} retrying={query.isFetching} />
        </div>
        {back}
      </section>
    );
  }

  if (!query.data) {
    return (
      <section className="min-w-0">
        <PageHeader title={t('activities.detail.title')} />
        <LoadingStatus label={t('activities.detail.loading')} />
      </section>
    );
  }

  return <DetailBody activity={query.data} back={back} />;
}

function isNotFound(error: unknown): boolean {
  return error instanceof ActivitiesRequestError && error.kind === 'not-found';
}

function DetailBody({ activity, back }: { activity: ActivityDetail; back: ReactNode }) {
  const { t } = useTranslation();
  const { distance, duration, elevation, metric, dateTime } = useFormatters();
  const sportLabel = useSportLabel();
  // Il riepilogo può mancare del tutto in una risposta incompleta: ogni campo risulta «non disponibile».
  const summary = activity.summary as ActivityDetail['summary'] | undefined;

  return (
    <section className="min-w-0">
      <div className="-mt-2 mb-2">{back}</div>
      <PageHeader title={sportLabel(activity.sport)} description={<ActivityValue value={dateTime(activity.startTime)} />} />

      <h2 className="sr-only">{t('activities.detail.summaryLabel')}</h2>
      <dl className="grid min-w-0 grid-cols-2 gap-3 md:grid-cols-4">
        <Stat label={t('activities.detail.summary.distance')} value={distance(activity.distanceMeters)} />
        <Stat label={t('activities.detail.summary.duration')} value={duration(activity.elapsedSeconds)} />
        <Stat label={t('activities.detail.summary.movingTime')} value={duration(activity.movingSeconds)} />
        <Stat label={t('activities.detail.summary.elevation')} value={elevation(activity.elevationGainMeters)} />
        <Stat label={t('activities.detail.summary.avgHeartRate')} value={metric(summary?.avgHeartRate, 'bpm')} />
        <Stat label={t('activities.detail.summary.avgPower')} value={metric(summary?.avgPowerWatts, 'W')} />
        <Stat label={t('activities.detail.summary.avgCadence')} value={metric(summary?.avgCadence, 'rpm')} />
      </dl>

      <LapsSection laps={activity.laps} activityStart={activity.startTime} />
    </section>
  );
}

/** Un dato del riepilogo: numero grande e leggibile con la sua etichetta; «non disponibile» se assente. */
function Stat({ label, value }: { label: string; value: string | null }) {
  return (
    <div className="min-w-0 rounded-xl border border-border bg-surface p-4">
      <dt className="text-sm text-muted">{label}</dt>
      <dd className="mt-1 text-2xl font-semibold tracking-tight break-words md:text-3xl">
        <span className={value === null ? 'text-base' : undefined}>
          <ActivityValue value={value} />
        </span>
      </dd>
    </div>
  );
}
