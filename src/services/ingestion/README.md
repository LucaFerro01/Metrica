# ingestion

Raccolta delle attività dalle fonti esterne e traduzione nel modello comune.

- **Responsabilità:** connettori `Strava`, `Zepp`, `FitGpx`; webhook (risposta immediata, elaborazione in background); import storico a ritmo controllato; parsing di FIT e GPX; deduplicazione.
- **Dati posseduti:** schema `ingestion` (stato di sincronizzazione, chiavi di deduplicazione, coda di lavoro, file grezzi).
- **Pubblica:** `ActivityImported`.
- **Consuma:** `ConnectionLinked`, `ConnectionRevoked`, `UserDeleted`.
- **Origine nel PoC:** `strava.service.ts`, `zepp.service.ts`, `fitgpx.service.ts`, `strava.worker.ts`, `webhook.routes.ts`.
- **Stato:** implementato solo il parsing (`TriMetric.Ingestion`: `IActivityFileParser`, `GpxParser`, `FitParser`, modello `ParsedActivity`, errori `ActivityFileException` e `TooManyPointsException`; test in `tests/TriMetric.Ingestion.Tests`). Host web, upload, deduplicazione e persistenza da implementare.

**Regole dei parser** (fissate dai test in `tests/TriMetric.Ingestion.Tests`, salvo dove indicato):
- GPX con `XmlReader` indurito letto in avanti a ciclo lineare (nessun DTD, nessun resolver, niente `XElement`): profondita' di annidamento massima 32, altrimenti `ActivityFileException` (un annidamento di decine di migliaia di livelli manderebbe in stack overflow un parser ricorsivo e ucciderebbe il processo). FIT con `Garmin.FIT.Sdk` confinato in `FitParser`, con controllo di intestazione e CRC; input FIT al massimo 32 MiB in memoria.
- Stream a colonne arrotondato secondo DEC-012. Oltre 100.000 punti `TooManyPointsException` (100.000 ammessi). Tetti: al massimo 1.000 giri (per GPX 1.000 segmenti non vuoti) e 100 sessioni FIT, altrimenti `ActivityFileException`; con 100.000 punti a tutte le colonne e 1.000 giri l'evento resta sotto 8 MiB (test).
- Valori fuori scala = assenti: quota -1.000..20.000 m, FC 1..300 (lo 0 e' il valore dei dispositivi senza lettura e abbasserebbe la media: e' trattato come assente), potenza 0..5.000 W, cadenza 0..300, velocita' 0..100 m/s. Il valore "non valido" dei tipi FIT (0xFF, 0xFFFF, ...) e' assente. Istanti fuori da 1980-2100: file rifiutato. Tutti gli aggregati devono essere finiti, cosi' ogni `ParsedActivity` si serializza con `ContractJson.Options`.
- Offset di tempo non decrescenti: punti con lo stesso istante restano entrambi.
- Tempo in movimento: intervalli con velocita' almeno 0,5 m/s (test di confine a 0,499 e 0,501 m/s).
- Dislivello: per il GPX e' la somma delle salite tra quote consecutive sui valori grezzi, senza smoothing (si gonfia con una quota rumorosa); per un FIT con una sola sessione si usa l'ascesa totale del dispositivo, con ripiego sul calcolo dai record.
- Giri GPX: uno per `trkseg` non vuoto se i segmenti sono piu' di uno (un GPX con un `trkseg` per ogni pausa da' un giro per pausa), altrimenti nessuno. FIT: un giro per messaggio `Lap`.
- FIT con piu' sessioni: un'attivita' `other` con tempi, distanza e ascesa sommati. Il token di cancellazione e' controllato durante tutta la lettura.
- I parser non scrivono log, i messaggi delle eccezioni non contengono dati del file e non hanno stato: si possono registrare come singleton (usati da 8 thread senza differenze nei risultati; verificato dal revisore e da un test).

Vedi [`07-integrations.md`](../../../ARCHITECTURE/07-integrations.md).
