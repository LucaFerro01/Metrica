import { useQueries, useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { activityKeys, fetchActivityExists } from '../data/activitiesApi';
import { pendingUploads, usePendingUploads } from '../pending/pendingUploads';

/** Intervallo del controllo «l'attività è comparsa?» finché l'upload è in elaborazione (DEC-008). */
export const PENDING_POLL_INTERVAL_MS = 2000;

/**
 * Oltre questo tempo dall'accettazione (`acceptedAt`) il polling si ferma e la riga diventa «ancora in
 * elaborazione, riprova». 60 s: l'importazione dei file normali richiede pochi secondi (misurati circa 3-5 s
 * per 100.000 punti), quindi un minuto copre anche i ritardi di consegna senza interrogare il server
 * all'infinito.
 */
export const PENDING_TIMEOUT_MS = 60_000;

export interface TrackedUpload {
  externalId: string;
  acceptedAt: number;
  /**
   * Il timeout è scaduto E l'ultimo controllo automatico non ha trovato l'attività: nessun polling, solo il
   * controllo singolo di `recheck`. Finché quel controllo è in corso la riga resta «in elaborazione».
   */
  timedOut: boolean;
  /** Un controllo è in corso. */
  checking: boolean;
  /** Controllo singolo (pulsante «Riprova» della riga scaduta); se l'attività esiste la risolve. */
  recheck: () => void;
}

/** Ultimo evento da annunciare agli screen reader (regione live sempre presente nella pagina). */
export type PendingAnnouncement = 'imported' | 'timedOut' | null;

export interface PendingTracking {
  uploads: TrackedUpload[];
  announcement: PendingAnnouncement;
}

/**
 * Un orologio che si aggiorna solo alla prossima scadenza (nessun intervallo permanente) e quando la scheda
 * torna visibile: i timer di una scheda in secondo piano (telefono) possono essere rallentati o sospesi, e al
 * ritorno una scadenza già passata va riconosciuta subito.
 */
function useNow(deadlines: readonly number[]): number {
  const [now, setNow] = useState(() => Date.now());
  const upcoming = deadlines.filter((deadline) => deadline > now);
  const next = upcoming.length > 0 ? Math.min(...upcoming) : undefined;

  useEffect(() => {
    if (next === undefined) return;
    // `Math.max`: un timer può scattare in anticipo di 1 ms rispetto a `Date.now()`; senza questo `now` resterebbe
    // appena sotto la scadenza, `next` non cambierebbe e il timer non verrebbe riarmato (polling mai fermato).
    const timer = setTimeout(() => setNow(Math.max(Date.now(), next)), Math.max(0, next - Date.now()));
    return () => clearTimeout(timer);
  }, [next]);

  useEffect(() => {
    const onVisibilityChange = () => {
      if (document.visibilityState === 'visible') setNow((current) => Math.max(Date.now(), current));
    };
    document.addEventListener('visibilitychange', onVisibilityChange);
    return () => document.removeEventListener('visibilitychange', onVisibilityChange);
  }, []);

  return now;
}

/**
 * Segue gli upload accettati e non ancora comparsi nell'elenco (`pendingUploads`, DEC-008/DEC-017).
 *
 * Un solo meccanismo: per ogni upload una query `GET /activities?externalId=<id>&pageSize=1` (rilevamento
 * esatto, non una ricerca nella pagina visibile). Finché non è scaduto si ripete ogni 2 s (TanStack Query lo
 * sospende a scheda nascosta), al massimo una richiesta per upload e per ciclo; con lo store vuoto non esiste
 * nessuna query, quindi nessuna richiesta.
 *
 * Alla scadenza (60 s da `acceptedAt`, anche se scoperta al ritorno in primo piano) il polling si ferma e si
 * esegue UN ultimo controllo automatico per upload: il tempo scorre anche a scheda nascosta mentre il polling
 * è sospeso, quindi senza questo controllo si direbbe «ancora in elaborazione» senza aver chiesto al server.
 * Solo se l'attività ancora non c'è (o il controllo fallisce) la riga diventa «ancora in elaborazione, riprova».
 * Un upload già scaduto all'apertura dell'elenco usa il controllo di montaggio come ultimo controllo.
 *
 * Quando l'attività esiste si invalida l'elenco (che si riscarica, senza polling proprio) e solo dopo si toglie
 * l'upload dallo store, così la riga «in elaborazione» non sparisce prima che compaia l'attività vera.
 */
export function usePendingTracking(): PendingTracking {
  const pending = usePendingUploads();
  const queryClient = useQueryClient();
  const now = useNow(pending.map((item) => item.acceptedAt + PENDING_TIMEOUT_MS));
  const resolving = useRef(new Set<string>());
  const finalCheckStarted = useRef(new Set<string>());
  const [finalCheckFailed, setFinalCheckFailed] = useState<ReadonlySet<string>>(() => new Set());
  const [announcement, setAnnouncement] = useState<PendingAnnouncement>(null);

  const isExpired = (acceptedAt: number) => now - acceptedAt >= PENDING_TIMEOUT_MS;

  const queries = useQueries({
    queries: pending.map(({ externalId, acceptedAt }) => ({
      queryKey: activityKeys.byExternalId(externalId),
      queryFn: ({ signal }: { signal: AbortSignal }) => fetchActivityExists(externalId, signal),
      refetchInterval: isExpired(acceptedAt) ? (false as const) : PENDING_POLL_INTERVAL_MS,
      retry: false,
      gcTime: 0,
    })),
  });

  // Ultimo controllo automatico, una sola volta per upload. `fetchQuery` si unisce alla richiesta eventualmente
  // già in corso (il controllo di montaggio di un upload scaduto all'apertura): una sola richiesta in ogni caso.
  useEffect(() => {
    for (const { externalId, acceptedAt } of pending) {
      if (now - acceptedAt < PENDING_TIMEOUT_MS || finalCheckStarted.current.has(externalId)) continue;
      finalCheckStarted.current.add(externalId);
      const markNotFound = () => {
        setFinalCheckFailed((previous) => new Set(previous).add(externalId));
        setAnnouncement('timedOut');
      };
      queryClient
        .fetchQuery({
          queryKey: activityKeys.byExternalId(externalId),
          queryFn: ({ signal }) => fetchActivityExists(externalId, signal),
          staleTime: 0,
        })
        // Trovata: la risolve l'effetto qui sotto (la riga non passa mai da «ancora in elaborazione»).
        .then((found) => (found ? undefined : markNotFound()), markNotFound);
    }
  }, [pending, now, queryClient]);

  useEffect(() => {
    queries.forEach((query, index) => {
      const item = pending[index];
      if (!item || query.data !== true || resolving.current.has(item.externalId)) return;
      resolving.current.add(item.externalId);
      void queryClient.invalidateQueries({ queryKey: activityKeys.lists() }).finally(() => {
        resolving.current.delete(item.externalId);
        pendingUploads.remove(item.externalId);
        setAnnouncement('imported');
      });
    });
  }, [queries, pending, queryClient]);

  const uploads = pending.map((item, index) => {
    const query = queries[index];
    return {
      externalId: item.externalId,
      acceptedAt: item.acceptedAt,
      timedOut: isExpired(item.acceptedAt) && finalCheckFailed.has(item.externalId),
      checking: query?.isFetching ?? false,
      recheck: () => void query?.refetch(),
    };
  });
  return { uploads, announcement };
}
