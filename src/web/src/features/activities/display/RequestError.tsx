import { CircleAlert } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/shared/ui';
import { ActivitiesRequestError, type RequestErrorKind } from '../data/activitiesApi';

const MESSAGE_KEYS = {
  network: 'activities.errors.network',
  server: 'activities.errors.server',
  'bad-request': 'activities.errors.badRequest',
  // Un 404 non dovrebbe arrivare qui (il dettaglio lo gestisce a parte): messaggio generico.
  'not-found': 'activities.errors.unexpected',
  unexpected: 'activities.errors.unexpected',
} as const satisfies Record<RequestErrorKind, string>;

interface RequestErrorProps {
  error: unknown;
  onRetry: () => void;
  /** Un nuovo tentativo è in corso: il pulsante si disabilita (niente richieste doppie). */
  retrying?: boolean;
}

/** Errore di caricamento con «Riprova». Il testo dipende solo dal tipo di errore, mai dal server. */
export function RequestError({ error, onRetry, retrying = false }: RequestErrorProps) {
  const { t } = useTranslation();
  const kind = error instanceof ActivitiesRequestError ? error.kind : 'unexpected';
  return (
    <div role="alert" className="flex min-w-0 flex-col items-start gap-3 rounded-lg border border-danger bg-surface p-4">
      <div className="flex min-w-0 items-start gap-3">
        <CircleAlert aria-hidden="true" className="mt-0.5 size-5 shrink-0 text-danger" />
        <p className="min-w-0">{t(MESSAGE_KEYS[kind])}</p>
      </div>
      <Button variant="secondary" onClick={onRetry} disabled={retrying}>
        {t('activities.errors.retry')}
      </Button>
    </div>
  );
}
