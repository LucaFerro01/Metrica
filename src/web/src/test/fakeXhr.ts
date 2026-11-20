import { vi } from 'vitest';

/**
 * `XMLHttpRequest` finto e controllabile per i test dell'upload (jsdom non fa rete vera e non emette eventi
 * di avanzamento). Registra le richieste fatte e permette al test di emettere avanzamento, rispondere,
 * simulare un errore di rete; `abort()` si comporta come quello vero (evento `abort` se la richiesta è in volo).
 */
export class FakeXhr extends EventTarget {
  static instances: FakeXhr[] = [];

  /** Ultima richiesta aperta; fallisce se non ce n'è nessuna (così un test che si aspetta una richiesta lo dice). */
  static last(): FakeXhr {
    const xhr = FakeXhr.instances.at(-1);
    if (!xhr) throw new Error('Nessuna richiesta XHR è stata fatta.');
    return xhr;
  }

  readonly upload = new EventTarget();
  method = '';
  url = '';
  body: Document | XMLHttpRequestBodyInit | null = null;
  status = 0;
  responseText = '';
  aborted = false;
  private inFlight = false;

  constructor() {
    super();
    FakeXhr.instances.push(this);
  }

  open(method: string, url: string): void {
    this.method = method;
    this.url = url;
  }

  send(body?: Document | XMLHttpRequestBodyInit | null): void {
    this.body = body ?? null;
    this.inFlight = true;
  }

  abort(): void {
    if (!this.inFlight) return;
    this.inFlight = false;
    this.aborted = true;
    this.dispatchEvent(new ProgressEvent('abort'));
  }

  // --- controllo dal test -------------------------------------------------------------------

  /** Avanzamento dell'invio (come `xhr.upload.onprogress`). */
  emitProgress(loaded: number, total: number): void {
    this.upload.dispatchEvent(new ProgressEvent('progress', { lengthComputable: true, loaded, total }));
  }

  /** Avanzamento senza lunghezza calcolabile. */
  emitIndeterminateProgress(loaded: number): void {
    this.upload.dispatchEvent(new ProgressEvent('progress', { lengthComputable: false, loaded, total: 0 }));
  }

  /** Risposta del server con un corpo (oggetto → JSON, stringa → testo così com'è). */
  respond(status: number, body: unknown = ''): void {
    this.inFlight = false;
    this.status = status;
    this.responseText = typeof body === 'string' ? body : JSON.stringify(body);
    this.dispatchEvent(new ProgressEvent('load'));
  }

  /** Errore di rete (connessione chiusa, rifiuto, CORS): nessuna risposta leggibile. */
  failNetwork(): void {
    this.inFlight = false;
    this.status = 0;
    this.dispatchEvent(new ProgressEvent('error'));
  }

  /** Il campo `file` del `FormData` inviato. */
  sentFile(): FormDataEntryValue | null {
    return this.body instanceof FormData ? this.body.get('file') : null;
  }
}

/** Sostituisce `XMLHttpRequest` con `FakeXhr` per il test corrente (ripristinato da `unstubGlobals`). */
export function installFakeXhr(): typeof FakeXhr {
  FakeXhr.instances = [];
  vi.stubGlobal('XMLHttpRequest', FakeXhr);
  return FakeXhr;
}
