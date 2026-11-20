import { API_BASE, ingestionBaseUrl } from '@/shared/api/config';
import type { components, operations } from '@/shared/api/ingestion';

// Invio di un file a `POST <base ingestion>/uploads` con avanzamento (REQ-026).
//
// Si usa `XMLHttpRequest` e non `fetch` perché `fetch` non riporta l'avanzamento dell'INVIO. Il modulo non
// lancia mai e non scrive nulla sulla console: ogni esito è un valore (`UploadResult`), compresi
// l'annullamento, l'errore di rete e le risposte inattese. Del server si usa solo il codice `type` del
// ProblemDetails (mai `title`/`detail`, in italiano fisso: i testi mostrati sono tradotti dall'interfaccia).
// Nessun timeout lato client (`xhr.timeout` non è impostato, quindi non c'è l'evento `timeout`): un invio
// bloccato dalla rete lo interrompe «Annulla» e, lato server, l'inattività la chiude il gateway.

type UploadResponse = components['schemas']['UploadResponse'];
// Il corpo è multipart con il solo campo `file` (tipo generato: `file: string`, binary).
type UploadBodyField = keyof operations['UploadActivityFile']['requestBody']['content']['multipart/form-data'];
const FILE_FIELD: UploadBodyField = 'file';

const PROBLEM_TYPE_PREFIX = 'urn:trimetric:problem:';

/** Codici `type` dei ProblemDetails di `POST /uploads` (ingestion, `UploadProblems.cs`). */
export const PROBLEM_CODES = [
  'invalid-file-field',
  'malformed-request',
  'empty-file',
  'unsupported-file-type',
  'file-too-large',
  'invalid-activity-file',
  'too-many-points',
  'parse-timeout',
  'event-too-large',
  'internal-error',
] as const;
export type ProblemCode = (typeof PROBLEM_CODES)[number];

export type UploadStatus = 'accepted' | 'duplicate';

export type UploadFailure =
  /** L'utente (o l'uscita dalla pagina) ha interrotto l'invio. */
  | { reason: 'aborted' }
  /** Nessuna risposta: connessione interrotta, rifiutata, chiusa dal gateway (413 di un file grande). */
  | { reason: 'network' }
  /** Errore con `ProblemDetails` il cui `type` è uno dei codici noti. */
  | { reason: 'problem'; code: ProblemCode; httpStatus: number }
  /** Errore HTTP senza codice riconoscibile (corpo vuoto o non JSON, type sconosciuto, risposta del gateway). */
  | { reason: 'http'; httpStatus: number }
  /** Risposta di successo con forma o stato inattesi. */
  | { reason: 'unexpected'; httpStatus: number };

export type UploadResult = ({ ok: true; status: UploadStatus } & Pick<UploadResponse, 'uploadId' | 'externalId'>) | ({ ok: false } & UploadFailure);

export interface UploadOptions {
  /** Percentuale intera 0–100 dei byte inviati, oppure `null` se il browser non conosce la lunghezza (barra indeterminata). */
  onProgress?: (percent: number | null) => void;
  /** Annulla l'invio (esito `aborted`); se è già annullato non parte nessuna richiesta. */
  signal?: AbortSignal;
  /** Base dell'API (predefinita: `VITE_API_BASE_URL` normalizzata); i test usano un URL assoluto. */
  apiBase?: string;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function parseJson(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return undefined;
  }
}

function isUploadStatus(value: unknown): value is UploadStatus {
  return value === 'accepted' || value === 'duplicate';
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value !== '';
}

/** Type guard sull'esito di successo: `status` è `string` nel tipo generato, qui lo si restringe. */
function toSuccess(body: unknown): { status: UploadStatus; uploadId: string; externalId: string } | null {
  if (!isRecord(body)) return null;
  const { uploadId, externalId, status } = body;
  if (!isNonEmptyString(uploadId) || !isNonEmptyString(externalId) || !isUploadStatus(status)) return null;
  return { status, uploadId, externalId };
}

function isProblemCode(value: string): value is ProblemCode {
  return (PROBLEM_CODES as readonly string[]).includes(value);
}

/** Il codice del ProblemDetails (`urn:trimetric:problem:<codice>`) se riconosciuto, altrimenti `null`. */
function toProblemCode(body: unknown): ProblemCode | null {
  if (!isRecord(body) || typeof body.type !== 'string') return null;
  if (!body.type.startsWith(PROBLEM_TYPE_PREFIX)) return null;
  const code = body.type.slice(PROBLEM_TYPE_PREFIX.length);
  return isProblemCode(code) ? code : null;
}

function toResult(httpStatus: number, responseText: string): UploadResult {
  const body = parseJson(responseText);

  if (httpStatus === 200 || httpStatus === 202) {
    const success = toSuccess(body);
    return success ? { ok: true, ...success } : { ok: false, reason: 'unexpected', httpStatus };
  }
  if (httpStatus >= 200 && httpStatus < 300) return { ok: false, reason: 'unexpected', httpStatus };

  const code = toProblemCode(body);
  return code
    ? { ok: false, reason: 'problem', code, httpStatus }
    : { ok: false, reason: 'http', httpStatus };
}

/**
 * Invia `file` (campo multipart `file`) a `<base>/ingestion/uploads`. Non imposta `Content-Type`: lo mette il
 * browser, con il boundary. Non lancia mai: la promessa si risolve sempre con un `UploadResult`.
 */
export function uploadFile(file: File, options: UploadOptions = {}): Promise<UploadResult> {
  const { onProgress, signal, apiBase = API_BASE } = options;

  return new Promise<UploadResult>((resolve) => {
    if (signal?.aborted) {
      resolve({ ok: false, reason: 'aborted' });
      return;
    }

    const xhr = new XMLHttpRequest();
    let settled = false;

    const finish = (result: UploadResult) => {
      if (settled) return;
      settled = true;
      signal?.removeEventListener('abort', onSignalAbort);
      resolve(result);
    };
    const onSignalAbort = () => xhr.abort();

    xhr.upload.addEventListener('progress', (event) => {
      if (settled || !onProgress) return;
      const computable = event.lengthComputable && event.total > 0;
      onProgress(computable ? Math.min(100, Math.round((event.loaded / event.total) * 100)) : null);
    });
    xhr.addEventListener('load', () => finish(toResult(xhr.status, xhr.responseText)));
    xhr.addEventListener('error', () => finish({ ok: false, reason: 'network' }));
    xhr.addEventListener('abort', () => finish({ ok: false, reason: 'aborted' }));
    signal?.addEventListener('abort', onSignalAbort);

    try {
      const body = new FormData();
      body.append(FILE_FIELD, file);
      xhr.open('POST', `${ingestionBaseUrl(apiBase)}/uploads`);
      xhr.responseType = 'text';
      xhr.send(body);
    } catch {
      // Base URL non valida o stato dell'XHR inatteso: nessuna richiesta è partita, si riporta come errore di rete.
      finish({ ok: false, reason: 'network' });
    }
  });
}
