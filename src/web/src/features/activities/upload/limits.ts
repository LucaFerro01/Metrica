// Limiti e validazione del file PRIMA dell'invio (REQ-026, AC-004, rilievo I1 di T14).

/**
 * Dimensione massima di un file caricabile: 32 MiB = 33.554.432 byte. Deve coincidere con `Upload:MaxBytes`
 * di ingestion (`src/services/ingestion`, predefinito 32 MiB). Il controllo sul client è indispensabile:
 * attraverso il gateway il 413 di un file grande NON è leggibile dal browser (il gateway chiude la
 * connessione senza leggere il corpo e il browser vede solo un errore di rete), quindi un file più grande
 * si rifiuta qui, prima di inviarlo. Esattamente 32 MiB è ammesso.
 */
export const MAX_UPLOAD_BYTES = 32 * 1024 * 1024;

/**
 * Sopra questa dimensione un errore di rete durante l'invio mostra, oltre a «connessione interrotta», il
 * suggerimento di riprovare con una rete più stabile (e il limite). La soglia di 8 MiB è ARBITRARIA (scelta di
 * T17): serve solo a distinguere gli invii lunghi, dove una rete instabile è più probabile, da quelli brevi.
 *
 * Non dice «file troppo grande» come causa: col limite del client uguale a quello del server
 * (`MAX_UPLOAD_BYTES` = `Upload:MaxBytes`, con il gateway che ammette 1 MiB di overhead in più), un file che
 * supera il controllo del client non può provocare il 413 illeggibile del gateway (rilievo I1 di T14: quello
 * nasce solo per file oltre il limite, che il client rifiuta prima). Un test verifica l'uguaglianza con
 * `appsettings.json` di ingestion; con un server configurato con un limite più basso il file verrebbe
 * rifiutato dal server (413 o errore di rete) e il messaggio resta comunque corretto.
 */
export const NETWORK_ERROR_LARGE_FILE_BYTES = 8 * 1024 * 1024;

/** Estensioni ammesse (confronto senza distinzione di maiuscole), come l'attributo `accept` dell'input. */
export const ACCEPTED_EXTENSIONS = ['.gpx', '.fit'] as const;
export const ACCEPT_ATTRIBUTE = ACCEPTED_EXTENSIONS.join(',');

export type FileValidationError = 'unsupported-type' | 'empty' | 'too-large';

/** Controlla estensione, dimensione zero e limite massimo (in questo ordine); `null` se il file si può inviare. */
export function validateUploadFile(file: { name: string; size: number }): FileValidationError | null {
  const name = file.name.toLowerCase();
  if (!ACCEPTED_EXTENSIONS.some((extension) => name.endsWith(extension))) return 'unsupported-type';
  if (!(file.size > 0)) return 'empty';
  if (file.size > MAX_UPLOAD_BYTES) return 'too-large';
  return null;
}
