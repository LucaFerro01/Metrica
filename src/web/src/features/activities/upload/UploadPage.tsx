import { Link } from '@tanstack/react-router';
import { CircleAlert, CircleCheck, FileUp, Info } from 'lucide-react';
import {
  useEffect,
  useId,
  useLayoutEffect,
  useRef,
  useState,
  type ChangeEvent,
  type DragEvent as ReactDragEvent,
} from 'react';
import { useTranslation } from 'react-i18next';
import { useFormatters } from '@/shared/i18n/useFormatters';
import { Button, PageHeader, cn } from '@/shared/ui';
import { pendingUploads } from '../pending/pendingUploads';
import { ACCEPT_ATTRIBUTE, MAX_UPLOAD_BYTES, validateUploadFile } from './limits';
import { uploadFile, type UploadStatus } from './uploadFile';
import { clientErrorKey, failureMessageKey, type UploadMessageKey } from './uploadMessages';

type Phase =
  /** `progress`: percentuale 0–100, oppure `null` se il browser non conosce la lunghezza (barra indeterminata). */
  | { kind: 'idle' }
  | { kind: 'uploading'; progress: number | null }
  | { kind: 'done'; status: UploadStatus }
  | { kind: 'failed'; messageKey: UploadMessageKey };

/** `true` se il trascinamento porta dei file (un testo o un link trascinati non si toccano). */
function carriesFiles(dataTransfer: DataTransfer | null | undefined): boolean {
  return Array.from(dataTransfer?.types ?? []).includes('Files');
}

/**
 * Pagina Carica (REQ-026): scelta di UN file `.gpx`/`.fit` con il normale `<input type="file">` (sul telefono
 * apre il selettore di sistema) e, da 768 px, anche trascinamento sulla stessa area. Il file si valida
 * PRIMA di inviarlo (estensione, vuoto, oltre 32 MiB: vedi `limits.ts`); l'invio mostra l'avanzamento e si
 * può annullare con «Annulla» (l'unico modo di interromperlo); l'esito è tradotto (mai testo del server) e
 * un upload accettato si registra in `pendingUploads` per mostrarlo «in elaborazione» nell'elenco (T18).
 *
 * Uscire dalla pagina durante l'invio NON interrompe la richiesta: se il server accetta (202) l'upload si
 * registra comunque; dopo lo smontaggio non si aggiorna nessuno stato React.
 */
export function UploadPage() {
  const { t } = useTranslation();
  const { fileSize } = useFormatters();
  const inputId = useId();
  const [file, setFile] = useState<File | null>(null);
  const [phase, setPhase] = useState<Phase>({ kind: 'idle' });
  const [dragging, setDragging] = useState(false);
  const controllerRef = useRef<AbortController | null>(null);
  const mountedRef = useRef(true);
  const previousKindRef = useRef<Phase['kind']>('idle');
  const sendRef = useRef<HTMLButtonElement>(null);
  const cancelRef = useRef<HTMLButtonElement>(null);
  const outcomeRef = useRef<HTMLDivElement>(null);
  const errorRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  // Un file lasciato FUORI dall'area di selezione non deve far navigare il browser (che aprirebbe il file al
  // posto della pagina): dragover e drop con dei file si bloccano a livello di window. L'area stessa gestisce i
  // suoi eventi prima (React sta sotto window): se ha già chiamato `preventDefault` non si fa altro, così il
  // cursore resta «copy» sull'area e diventa «none» fuori.
  useEffect(() => {
    const swallowFileDrag = (event: Event) => {
      const { dataTransfer } = event as globalThis.DragEvent;
      if (event.defaultPrevented || !carriesFiles(dataTransfer)) return;
      event.preventDefault();
      if (event.type === 'dragover' && dataTransfer) dataTransfer.dropEffect = 'none';
    };
    window.addEventListener('dragover', swallowFileDrag);
    window.addEventListener('drop', swallowFileDrag);
    return () => {
      window.removeEventListener('dragover', swallowFileDrag);
      window.removeEventListener('drop', swallowFileDrag);
    };
  }, []);

  // Focus da tastiera: i pulsanti cambiano (Annulla compare e sparisce), quindi si porta il focus dove serve.
  // `useLayoutEffect`: il focus passa nello stesso commit che mostra il nuovo elemento (senza attendere gli
  // effetti passivi), così non c'è un istante in cui il focus è perso sul body.
  useLayoutEffect(() => {
    const previous = previousKindRef.current;
    previousKindRef.current = phase.kind;
    if (previous === phase.kind) return;
    if (phase.kind === 'uploading') cancelRef.current?.focus();
    else if (phase.kind === 'done') outcomeRef.current?.focus();
    else if (phase.kind === 'failed') errorRef.current?.focus();
    else if (previous === 'uploading') sendRef.current?.focus(); // annullato: il file resta scelto
  }, [phase.kind]);

  const uploading = phase.kind === 'uploading';
  const clientError = file ? validateUploadFile(file) : null;
  const canSend = file !== null && clientError === null && !uploading;
  const maxSize = fileSize(MAX_UPLOAD_BYTES) ?? '';

  const errorKey: UploadMessageKey | null =
    phase.kind === 'failed' ? phase.messageKey : clientError ? clientErrorKey(clientError) : null;

  function selectFile(picked: File) {
    setFile(picked);
    setPhase({ kind: 'idle' });
  }

  function handleChange(event: ChangeEvent<HTMLInputElement>) {
    const picked = event.target.files?.[0];
    // Azzera l'input: così si può scegliere di nuovo lo stesso file (altrimenti `change` non scatta).
    event.target.value = '';
    if (picked) selectFile(picked);
  }

  function handleDragOver(event: ReactDragEvent<HTMLElement>) {
    if (!carriesFiles(event.dataTransfer)) return; // un testo trascinato: né blocco né evidenziazione
    event.preventDefault();
    if (event.dataTransfer) event.dataTransfer.dropEffect = uploading ? 'none' : 'copy';
    if (!uploading) setDragging(true);
  }

  function handleDragLeave(event: ReactDragEvent<HTMLElement>) {
    if (event.relatedTarget instanceof Node && event.currentTarget.contains(event.relatedTarget)) return;
    setDragging(false);
  }

  function handleDrop(event: ReactDragEvent<HTMLElement>) {
    if (!carriesFiles(event.dataTransfer)) return;
    event.preventDefault();
    setDragging(false);
    if (uploading) return;
    const dropped = event.dataTransfer?.files?.[0]; // UN solo file: se ne arrivano di più, il primo
    if (dropped) selectFile(dropped);
  }

  async function handleSend() {
    // Un invio è già in corso (anche due clic nello stesso istante, prima che la pagina si ridisegni).
    if (controllerRef.current) return;
    if (!file || !canSend) return;
    const controller = new AbortController();
    controllerRef.current = controller;
    setPhase({ kind: 'uploading', progress: 0 });

    const result = await uploadFile(file, {
      signal: controller.signal,
      onProgress: (progress) => {
        if (mountedRef.current) setPhase({ kind: 'uploading', progress });
      },
    });
    controllerRef.current = null;

    // Si registra anche se la pagina è stata lasciata nel frattempo: il server ha accettato il file.
    if (result.ok && result.status === 'accepted') {
      pendingUploads.add({ externalId: result.externalId, uploadId: result.uploadId, acceptedAt: Date.now() });
    }
    if (!mountedRef.current) return; // nessun aggiornamento di stato dopo lo smontaggio

    if (result.ok) {
      setFile(null); // niente doppio invio dello stesso file; il nome non resta accanto all'esito
      setPhase({ kind: 'done', status: result.status });
      return;
    }
    const messageKey = failureMessageKey(result, file.size);
    // Annullamento (`null`): nessun esito da mostrare, il file resta scelto e si può riprovare.
    setPhase(messageKey ? { kind: 'failed', messageKey } : { kind: 'idle' });
  }

  function handleCancel() {
    controllerRef.current?.abort();
  }

  const progress = phase.kind === 'uploading' ? phase.progress : 0;
  const progressText =
    progress === null
      ? t('activities.upload.progressSending')
      : progress >= 100
        ? t('activities.upload.progressProcessing')
        : t('activities.upload.progressValue', { percent: progress });

  return (
    <section className="mx-auto w-full max-w-xl min-w-0">
      <PageHeader title={t('activities.upload.title')} description={t('activities.upload.description')} />

      <div
        role="group"
        aria-label={t('activities.upload.dropzoneLabel')}
        data-dragging={dragging}
        onDragEnter={handleDragOver}
        onDragOver={handleDragOver}
        onDragLeave={handleDragLeave}
        onDrop={handleDrop}
        className={cn(
          'flex min-w-0 flex-col gap-4 rounded-xl border-2 border-dashed bg-surface p-4 transition-colors md:p-6',
          // Il bordo tratteggiato deve vedersi (`border-border` è quasi invisibile su `surface`): muted attenuato.
          dragging ? 'border-accent' : 'border-muted/60',
        )}
      >
        <div className="flex min-w-0 flex-wrap items-center gap-x-4 gap-y-2">
          <input
            id={inputId}
            type="file"
            accept={ACCEPT_ATTRIBUTE}
            disabled={uploading}
            onChange={handleChange}
            className="peer sr-only"
          />
          <label
            htmlFor={inputId}
            className={cn(
              'inline-flex min-h-11 min-w-11 cursor-pointer items-center justify-center gap-2 rounded-lg border border-border bg-background px-4 text-sm font-medium',
              'transition-colors hover:bg-surface peer-focus-visible:outline-2 peer-focus-visible:outline-offset-2 peer-focus-visible:outline-accent',
              'peer-disabled:pointer-events-none peer-disabled:opacity-50',
            )}
          >
            <FileUp aria-hidden="true" className="size-5 shrink-0" />
            {t('activities.upload.choose')}
          </label>
          <p className="hidden text-sm text-muted md:block">{t('activities.upload.dropHint')}</p>
        </div>

        <p className="text-sm text-muted">{t('activities.upload.formats', { max: maxSize })}</p>

        {file ? (
          <p className="flex min-w-0 flex-wrap items-baseline gap-x-3 gap-y-1">
            <span className="min-w-0 font-medium break-all">{file.name}</span>
            <span className="shrink-0 text-sm text-muted">{fileSize(file.size)}</span>
          </p>
        ) : (
          <p className="text-sm text-muted">{t('activities.upload.noFile')}</p>
        )}
      </div>

      <div className="mt-4 flex flex-wrap gap-3">
        <Button ref={sendRef} onClick={() => void handleSend()} disabled={!canSend}>
          {t('activities.upload.submit')}
        </Button>
        {uploading ? (
          <Button ref={cancelRef} variant="secondary" onClick={handleCancel}>
            {t('activities.upload.cancel')}
          </Button>
        ) : null}
      </div>

      {phase.kind === 'uploading' ? (
        <div className="mt-4">
          <div className="mb-1 flex items-baseline justify-between gap-3 text-sm">
            <span>{t('activities.upload.progressLabel')}</span>
            <span className="shrink-0">{progressText}</span>
          </div>
          {/* Indeterminata (lunghezza non nota): niente aria-valuenow, barra piena che pulsa. */}
          <div
            role="progressbar"
            aria-label={t('activities.upload.progressLabel')}
            aria-valuemin={0}
            aria-valuemax={100}
            aria-valuenow={progress === null ? undefined : progress}
            className="h-2 w-full overflow-hidden rounded-full bg-border"
          >
            <div
              className={cn(
                'h-full bg-accent',
                progress === null ? 'w-full animate-pulse motion-reduce:animate-none' : 'transition-[width]',
              )}
              style={progress === null ? undefined : { width: `${progress}%` }}
            />
          </div>
        </div>
      ) : null}

      {/* Regione di stato sempre presente: così gli screen reader annunciano l'esito quando compare. */}
      <div role="status" aria-live="polite">
        {phase.kind === 'done' ? (
          <div
            ref={outcomeRef}
            tabIndex={-1}
            className="mt-4 flex items-start gap-3 rounded-lg border border-border bg-surface p-4"
          >
            {phase.status === 'accepted' ? (
              <CircleCheck aria-hidden="true" className="mt-0.5 size-5 shrink-0 text-accent" />
            ) : (
              <Info aria-hidden="true" className="mt-0.5 size-5 shrink-0 text-accent" />
            )}
            <p className="min-w-0">
              {phase.status === 'accepted' ? t('activities.upload.accepted') : t('activities.upload.duplicate')}
            </p>
          </div>
        ) : null}
      </div>

      {errorKey ? (
        <div
          ref={errorRef}
          tabIndex={-1}
          role="alert"
          className="mt-4 flex items-start gap-3 rounded-lg border border-danger bg-surface p-4"
        >
          <CircleAlert aria-hidden="true" className="mt-0.5 size-5 shrink-0 text-danger" />
          <p className="min-w-0">{t(errorKey, { max: maxSize })}</p>
        </div>
      ) : null}

      {phase.kind === 'done' ? (
        <Link to="/activities" className="mt-3 inline-flex min-h-11 items-center font-medium text-accent underline">
          {t('activities.upload.goToActivities')}
        </Link>
      ) : null}
    </section>
  );
}
