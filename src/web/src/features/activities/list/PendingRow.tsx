import { LoaderCircle } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useFormatters } from '@/shared/i18n/useFormatters';
import { Button } from '@/shared/ui';
import type { TrackedUpload } from './usePendingTracking';

/**
 * Contenuto di una riga «in elaborazione» (DEC-008): un upload accettato di cui l'attività non è ancora
 * comparsa. Non c'è il nome del file (lo store non lo conserva). Dopo il timeout il testo diventa «ancora in
 * elaborazione» e il pulsante «Riprova» esegue un controllo singolo.
 */
export function PendingRowContent({ upload }: { upload: TrackedUpload }) {
  const { t } = useTranslation();
  const { dateTime } = useFormatters();
  const time = dateTime(upload.acceptedAt) ?? '';
  return (
    <div className="flex min-w-0 flex-wrap items-center justify-between gap-x-4 gap-y-2">
      <div className="min-w-0">
        <p className="flex items-center gap-2 font-medium">
          <LoaderCircle
            aria-hidden="true"
            className={
              upload.timedOut
                ? 'size-4 shrink-0 text-muted'
                : 'size-4 shrink-0 animate-spin text-accent motion-reduce:animate-none'
            }
          />
          <span className="min-w-0">
            {upload.timedOut ? t('activities.list.pending.timedOut') : t('activities.list.pending.title')}
          </span>
        </p>
        <p className="mt-1 text-sm text-muted">
          {upload.timedOut
            ? t('activities.list.pending.timedOutHint', { time })
            : t('activities.list.pending.description', { time })}
        </p>
      </div>
      {upload.timedOut ? (
        <Button variant="secondary" onClick={upload.recheck} disabled={upload.checking}>
          {upload.checking ? t('activities.list.pending.checking') : t('activities.list.pending.retry')}
        </Button>
      ) : null}
    </div>
  );
}
