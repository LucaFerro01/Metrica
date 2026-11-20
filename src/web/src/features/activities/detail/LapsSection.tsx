import { useTranslation } from 'react-i18next';
import { useFormatters } from '@/shared/i18n/useFormatters';
import type { ActivityDetail } from '../data/activitiesApi';
import { ActivityValue } from '../display/ActivityValue';

interface LapsSectionProps {
  laps: ActivityDetail['laps'] | undefined;
  /** Inizio dell'attività (ISO 8601): l'inizio di ogni giro si mostra relativo a questo istante. */
  activityStart: string;
}

/** Inizio del giro in secondi dall'inizio dell'attività; `null` se uno dei due istanti non è valido. */
function relativeStartSeconds(lapStart: string, activityStart: string): number | null {
  const seconds = (Date.parse(lapStart) - Date.parse(activityStart)) / 1000;
  return Number.isFinite(seconds) ? seconds : null;
}

/** Elenco dei giri: inizio relativo, durata, distanza. Nessun giro (o campo `laps` assente) = nessuna sezione. */
export function LapsSection({ laps, activityStart }: LapsSectionProps) {
  const { t } = useTranslation();
  const { distance, duration } = useFormatters();
  if (!Array.isArray(laps) || laps.length === 0) return null;

  const header = 'px-3 py-3 text-left font-medium md:px-4';
  const headerNumber = 'px-3 py-3 text-right font-medium md:px-4';
  const cell = 'px-3 py-3 md:px-4';
  return (
    <section className="mt-8 min-w-0">
      <h2 className="mb-3 text-lg font-semibold tracking-tight">{t('activities.detail.laps.title')}</h2>
      <div className="min-w-0 overflow-hidden rounded-xl border border-border bg-surface">
        <table className="w-full text-sm">
          <thead className="border-b border-border text-muted">
            <tr>
              <th scope="col" className={header}>
                {t('activities.detail.laps.columns.lap')}
              </th>
              <th scope="col" className={headerNumber}>
                {t('activities.detail.laps.columns.start')}
              </th>
              <th scope="col" className={headerNumber}>
                {t('activities.detail.laps.columns.duration')}
              </th>
              <th scope="col" className={headerNumber}>
                {t('activities.detail.laps.columns.distance')}
              </th>
            </tr>
          </thead>
          <tbody className="divide-y divide-border">
            {laps.map((lap, index) => (
              <tr key={index}>
                <th scope="row" className={`${cell} text-left font-medium`}>
                  {t('activities.detail.laps.lap', { number: index + 1 })}
                </th>
                <td className={`${cell} text-right break-words`}>
                  <ActivityValue value={duration(relativeStartSeconds(lap.startTime, activityStart))} />
                </td>
                <td className={`${cell} text-right break-words`}>
                  <ActivityValue value={duration(lap.elapsedSeconds)} />
                </td>
                <td className={`${cell} text-right break-words`}>
                  <ActivityValue value={distance(lap.distanceMeters)} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
