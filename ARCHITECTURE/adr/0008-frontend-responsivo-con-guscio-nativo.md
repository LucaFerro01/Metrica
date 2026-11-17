# ADR 0008 · Front-end responsivo con due layout, predisposto per un guscio nativo

- **Stato:** Accettata
- **Data:** 2026-11-17

## Contesto

L'uso sul telefono è quello dell'atleta: consultare l'allenamento di oggi, rivedere un'attività appena fatta, caricare un file. La pianificazione (calendario, libreria ed editor degli allenamenti) è un lavoro da PC e chi la fa può essere un allenatore oppure l'atleta stesso. L'autore sviluppa su Windows e ha un iPhone, senza Mac.

## Decisione

Un'unica applicazione React ([ADR 0005](0005-frontend-react.md)) con **due modalità di layout** sulle stesse pagine: telefono (navigazione in basso, schede, piano in sola lettura) e desktop (barra laterale, tabelle, editor). Lo scheletro del sistema è **predisposto** per un guscio nativo **Capacitor** (base URL dell'API configurabile, margini sicuri, nessuna dipendenza da funzioni solo da browser) ma non lo contiene: il guscio è un task a parte dopo lo scheletro. La PWA installabile e l'offline restano fuori dallo scheletro.

## Motivazioni

- Le azioni frequenti sul telefono sono di lettura più un selettore di file: non richiedono funzioni native.
- Un solo codice per due ambienti; il guscio riusa la stessa app quando servono store, notifiche affidabili o funzioni del telefono.
- Rimandare il guscio evita di far dipendere lo scheletro da un account a pagamento e da una compilazione iOS che oggi non si può provare.

## Conseguenze

- Per l'iPhone, il guscio richiede l'account Apple Developer (99 $/anno) e una compilazione su macchine macOS nel cloud; l'Android si compila da Windows. Fino ad allora l'iPhone usa il sito da Safari (o la PWA).
- Il gateway dovrà ammettere le origini del guscio (`capacitor://localhost`, `https://localhost`) con una configurazione CORS esplicita.
- Le guideline di Apple (4.2) possono respingere un'app che sia solo un sito impacchettato: per la pubblicazione serve almeno una funzione nativa reale.
- Il ruolo allenatore è fuori da questa decisione e cambierà `identity` e `training-plan`.

## Alternative considerate

- **Solo PWA:** nessuna uscita verso gli store; su iPhone le notifiche push web funzionano solo ad app aggiunta alla schermata Home.
- **App nativa React Native/Expo per il telefono e web per PC:** esperienza mobile migliore, ma due codici da mantenere. Su Windows si sviluppa con Expo Go sull'iPhone, ma le build installabili richiedono l'account Apple Developer e la compilazione nel cloud, e non esiste il simulatore iOS.
- **Due front-end web distinti:** raddoppia il lavoro senza i vantaggi di un'app nativa.
