import { NETWORK_ERROR_LARGE_FILE_BYTES, type FileValidationError } from './limits';
import type { ProblemCode, UploadFailure } from './uploadFile';

// Dall'esito tecnico al testo da mostrare: restituisce la CHIAVE di traduzione (mai testo del server, mai
// il nome del file). I testi stanno in `shared/i18n/locales/{it,en}.json` sotto `activities.upload.errors`.

const PROBLEM_KEYS = {
  'invalid-file-field': 'activities.upload.errors.problem.invalidFileField',
  'malformed-request': 'activities.upload.errors.problem.malformedRequest',
  'empty-file': 'activities.upload.errors.problem.emptyFile',
  'unsupported-file-type': 'activities.upload.errors.problem.unsupportedFileType',
  'file-too-large': 'activities.upload.errors.problem.fileTooLarge',
  'invalid-activity-file': 'activities.upload.errors.problem.invalidActivityFile',
  'too-many-points': 'activities.upload.errors.problem.tooManyPoints',
  'parse-timeout': 'activities.upload.errors.problem.parseTimeout',
  'event-too-large': 'activities.upload.errors.problem.eventTooLarge',
  'internal-error': 'activities.upload.errors.problem.internalError',
} as const satisfies Record<ProblemCode, string>;

/** Errori del client prima dell'invio: stessi testi dei corrispondenti errori del server. */
const CLIENT_KEYS = {
  'unsupported-type': PROBLEM_KEYS['unsupported-file-type'],
  empty: PROBLEM_KEYS['empty-file'],
  'too-large': PROBLEM_KEYS['file-too-large'],
} as const satisfies Record<FileValidationError, string>;

export type UploadMessageKey =
  | (typeof PROBLEM_KEYS)[ProblemCode]
  | 'activities.upload.errors.status.badRequest'
  | 'activities.upload.errors.status.badGateway'
  | 'activities.upload.errors.status.gatewayTimeout'
  | 'activities.upload.errors.status.clientError'
  | 'activities.upload.errors.status.serverError'
  | 'activities.upload.errors.status.unexpected'
  | 'activities.upload.errors.network.interrupted'
  | 'activities.upload.errors.network.interruptedLarge';

export function clientErrorKey(error: FileValidationError): UploadMessageKey {
  return CLIENT_KEYS[error];
}

/** Messaggio per un errore HTTP privo di codice riconoscibile: prima lo stato noto, poi la classe di stato. */
function statusKey(httpStatus: number): UploadMessageKey {
  if (httpStatus === 413) return PROBLEM_KEYS['file-too-large']; // anche il 413 del gateway (senza type di Metrica)
  if (httpStatus === 502) return 'activities.upload.errors.status.badGateway';
  if (httpStatus === 504) return 'activities.upload.errors.status.gatewayTimeout';
  if (httpStatus === 400) return 'activities.upload.errors.status.badRequest';
  if (httpStatus >= 500) return 'activities.upload.errors.status.serverError';
  if (httpStatus >= 400) return 'activities.upload.errors.status.clientError';
  return 'activities.upload.errors.status.unexpected';
}

/**
 * Chiave del messaggio per un invio fallito, o `null` per l'annullamento (nessun esito da mostrare).
 * `fileSize` serve solo a dare un suggerimento in più: un errore di rete con un file oltre 8 MiB resta
 * «connessione interrotta» (testo neutro, senza dare la colpa alla dimensione) e aggiunge di riprovare con
 * una rete più stabile e il limite. Vedi `NETWORK_ERROR_LARGE_FILE_BYTES` in `limits.ts`.
 */
export function failureMessageKey(failure: UploadFailure, fileSize: number): UploadMessageKey | null {
  switch (failure.reason) {
    case 'aborted':
      return null;
    case 'network':
      return fileSize > NETWORK_ERROR_LARGE_FILE_BYTES
        ? 'activities.upload.errors.network.interruptedLarge'
        : 'activities.upload.errors.network.interrupted';
    case 'problem':
      return PROBLEM_KEYS[failure.code];
    case 'http':
      return statusKey(failure.httpStatus);
    case 'unexpected':
      return 'activities.upload.errors.status.unexpected';
  }
}
