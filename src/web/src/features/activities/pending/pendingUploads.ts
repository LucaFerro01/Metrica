import { useSyncExternalStore } from 'react';

// Upload accettati dal server e non ancora (necessariamente) comparsi nell'elenco (T17 → T18).
//
// Piccolo store in memoria, condiviso tra la pagina Carica (che registra) e l'elenco (che mostra le righe
// «in elaborazione» e le toglie quando l'attività compare, corrispondenza su `externalId`).
// SOLO IN MEMORIA, di proposito: niente `localStorage`/`sessionStorage` (la feature non può usarli e
// il guscio nativo non li garantisce), quindi ricaricare la pagina azzera lo stato; l'elenco vero dal
// server resta la fonte di verità. Contiene solo identificativi e un istante: mai il nome del file né il suo contenuto.

export interface PendingUpload {
  /** SHA-256 del contenuto: identificativo dell'attività (`externalId` in `activities`). */
  externalId: string;
  /** Identificativo del caricamento restituito da ingestion. */
  uploadId: string;
  /**
   * Istante in cui il server ha accettato il caricamento (ricezione del 202), in millisecondi dall'epoca
   * (`Date.now()`): da qui parte il timeout «ancora in elaborazione» di T18. NON è l'istante di avvio
   * dell'invio (un file grande può impiegare a lungo a salire).
   */
  acceptedAt: number;
}

let snapshot: readonly PendingUpload[] = [];
const listeners = new Set<() => void>();

function publish(next: readonly PendingUpload[]): void {
  snapshot = next;
  for (const listener of [...listeners]) listener();
}

export const pendingUploads = {
  /** Registra un upload accettato. Lo stesso `externalId` già presente non si duplica (resta il primo). */
  add({ externalId, uploadId, acceptedAt }: PendingUpload): void {
    if (snapshot.some((item) => item.externalId === externalId)) return;
    publish([...snapshot, { externalId, uploadId, acceptedAt }]);
  },
  /** Toglie l'upload con quell'`externalId` (es. quando l'attività è comparsa o è scaduto il timeout). */
  remove(externalId: string): void {
    if (!snapshot.some((item) => item.externalId === externalId)) return;
    publish(snapshot.filter((item) => item.externalId !== externalId));
  },
  clear(): void {
    if (snapshot.length === 0) return;
    publish([]);
  },
  /** Istantanea corrente: un nuovo array a ogni modifica, lo stesso riferimento se nulla è cambiato. */
  getSnapshot(): readonly PendingUpload[] {
    return snapshot;
  },
  subscribe(listener: () => void): () => void {
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
    };
  },
};

/** Gli upload in sospeso, in ordine di registrazione; il componente si aggiorna a ogni modifica. */
export function usePendingUploads(): readonly PendingUpload[] {
  return useSyncExternalStore(pendingUploads.subscribe, pendingUploads.getSnapshot, pendingUploads.getSnapshot);
}
