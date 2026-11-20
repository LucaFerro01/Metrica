import { Link, useNavigate } from '@tanstack/react-router';
import type { MouseEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useFormatters } from '@/shared/i18n/useFormatters';
import type { ActivitiesSearch } from '../data/activitiesSearch';
import type { ActivityListItem } from '../data/activitiesApi';
import { ActivityValue } from '../display/ActivityValue';
import { useSportLabel } from '../display/useSportLabel';
import { PendingRowContent } from './PendingRow';
import type { TrackedUpload } from './usePendingTracking';

// Le due viste dello stesso elenco (REQ-037, DEC-015): schede sotto 768 px e tabella da 768 px. Sono DUE
// rendering con lo stesso contenuto, alternati dalle classi (`md:hidden` / `hidden md:block`): quello non
// visibile è `display: none`, quindi fuori dall'albero di accessibilità e senza contenuto duplicato per l'utente.

interface RowsProps {
  items: readonly ActivityListItem[];
  /** Righe «in elaborazione» da mostrare in cima (già filtrate dalla pagina). */
  pending: readonly TrackedUpload[];
  /** Filtro e pagina correnti, riportati nel link al dettaglio per il «Torna all'elenco». */
  search: ActivitiesSearch;
}

function useRowValues() {
  const { distance, duration, elevation, dateTime } = useFormatters();
  const sportLabel = useSportLabel();
  return (item: ActivityListItem) => ({
    date: dateTime(item.startTime),
    sport: sportLabel(item.sport),
    distance: distance(item.distanceMeters),
    duration: duration(item.elapsedSeconds),
    elevation: elevation(item.elevationGainMeters),
  });
}

export function ActivityCards({ items, pending, search }: RowsProps) {
  const { t } = useTranslation();
  const values = useRowValues();
  return (
    <ul aria-label={t('activities.list.listLabel')} className="grid min-w-0 gap-3 md:hidden">
      {pending.map((upload) => (
        <li key={upload.externalId} className="min-w-0 rounded-xl border border-dashed border-muted/60 bg-surface p-4">
          <PendingRowContent upload={upload} />
        </li>
      ))}
      {items.map((item) => {
        const row = values(item);
        return (
          <li key={item.id} className="min-w-0">
            <Link
              to="/activities/$activityId"
              params={{ activityId: item.id }}
              search={search}
              className="block min-w-0 rounded-xl border border-border bg-surface p-4 hover:bg-background focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
            >
              <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
                <span className="min-w-0 font-semibold">{row.sport}</span>
                <span className="min-w-0 text-sm text-muted">
                  <ActivityValue value={row.date} />
                </span>
              </div>
              <dl className="mt-3 grid grid-cols-3 gap-3">
                <Metric label={t('activities.list.columns.distance')} value={row.distance} />
                <Metric label={t('activities.list.columns.duration')} value={row.duration} />
                <Metric label={t('activities.list.columns.elevation')} value={row.elevation} />
              </dl>
            </Link>
          </li>
        );
      })}
    </ul>
  );
}

function Metric({ label, value }: { label: string; value: string | null }) {
  return (
    <div className="min-w-0">
      <dt className="text-xs text-muted">{label}</dt>
      <dd className="mt-0.5 text-base font-semibold break-words">
        <ActivityValue value={value} />
      </dd>
    </div>
  );
}

export function ActivityTable({ items, pending, search }: RowsProps) {
  const { t } = useTranslation();
  const values = useRowValues();
  const navigate = useNavigate();

  /** Tutta la riga è cliccabile col mouse; la tastiera e gli screen reader usano il link della prima cella. */
  function openRow(event: MouseEvent<HTMLTableRowElement>, activityId: string) {
    // Il clic sul link è già gestito dal link (preventDefault): niente seconda navigazione.
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
      return;
    }
    // Chi sta selezionando del testo (per copiarlo) non vuole aprire il dettaglio.
    if ((window.getSelection()?.toString() ?? '') !== '') return;
    void navigate({ to: '/activities/$activityId', params: { activityId }, search });
  }

  const header = 'px-4 py-3 text-left font-medium';
  const headerNumber = 'px-4 py-3 text-right font-medium';
  return (
    <div className="hidden min-w-0 overflow-x-auto rounded-xl border border-border bg-surface md:block">
      <table aria-label={t('activities.list.listLabel')} className="w-full text-sm">
        <thead className="border-b border-border text-muted">
          <tr>
            <th scope="col" className={header}>
              {t('activities.list.columns.date')}
            </th>
            <th scope="col" className={header}>
              {t('activities.list.columns.sport')}
            </th>
            <th scope="col" className={headerNumber}>
              {t('activities.list.columns.distance')}
            </th>
            <th scope="col" className={headerNumber}>
              {t('activities.list.columns.duration')}
            </th>
            <th scope="col" className={headerNumber}>
              {t('activities.list.columns.elevation')}
            </th>
          </tr>
        </thead>
        <tbody className="divide-y divide-border">
          {pending.map((upload) => (
            <tr key={upload.externalId}>
              <td colSpan={5} className="px-4 py-3">
                <PendingRowContent upload={upload} />
              </td>
            </tr>
          ))}
          {items.map((item) => {
            const row = values(item);
            return (
              <tr
                key={item.id}
                onClick={(event) => openRow(event, item.id)}
                className="cursor-pointer hover:bg-background"
              >
                <td className="min-w-0 px-4 py-1 break-words">
                  <Link
                    to="/activities/$activityId"
                    params={{ activityId: item.id }}
                    search={search}
                    className="inline-flex min-h-11 items-center font-medium text-accent hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
                  >
                    <ActivityValue value={row.date} />
                  </Link>
                </td>
                <td className="px-4 py-1 break-words">{row.sport}</td>
                <td className="px-4 py-1 text-right break-words">
                  <ActivityValue value={row.distance} />
                </td>
                <td className="px-4 py-1 text-right break-words">
                  <ActivityValue value={row.duration} />
                </td>
                <td className="px-4 py-1 text-right break-words">
                  <ActivityValue value={row.elevation} />
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
