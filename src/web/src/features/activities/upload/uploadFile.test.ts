import { beforeEach, describe, expect, it, vi } from 'vitest';
import { FakeXhr, installFakeXhr } from '@/test/fakeXhr';
import { uploadFile } from './uploadFile';

const API = 'http://gateway.test/api';
const EXTERNAL_ID = 'c'.repeat(64);
const UPLOAD_ID = '33333333-3333-3333-3333-333333333333';
const file = new File(['<gpx/>'], 'corsa.gpx', { type: 'application/gpx+xml' });

beforeEach(() => {
  installFakeXhr();
});

describe('uploadFile: richiesta', () => {
  it('fa un POST multipart con il campo "file" verso <base ingestion>/uploads', () => {
    void uploadFile(file, { apiBase: API });
    const xhr = FakeXhr.last();
    expect(xhr.method).toBe('POST');
    expect(xhr.url).toBe('http://gateway.test/api/ingestion/uploads');
    expect(xhr.body).toBeInstanceOf(FormData);
    expect(xhr.sentFile()).toBeInstanceOf(File);
    expect((xhr.sentFile() as File).name).toBe('corsa.gpx');
  });

  it('senza apiBase usa la base configurata (/api/ingestion/uploads)', () => {
    void uploadFile(file);
    expect(FakeXhr.last().url).toBe('/api/ingestion/uploads');
  });
});

describe('uploadFile: esiti di successo', () => {
  it('202 accepted', async () => {
    const promise = uploadFile(file, { apiBase: API });
    FakeXhr.last().respond(202, { uploadId: UPLOAD_ID, externalId: EXTERNAL_ID, status: 'accepted' });
    await expect(promise).resolves.toEqual({
      ok: true,
      status: 'accepted',
      uploadId: UPLOAD_ID,
      externalId: EXTERNAL_ID,
    });
  });

  it('200 duplicate', async () => {
    const promise = uploadFile(file, { apiBase: API });
    FakeXhr.last().respond(200, { uploadId: UPLOAD_ID, externalId: EXTERNAL_ID, status: 'duplicate' });
    await expect(promise).resolves.toEqual({
      ok: true,
      status: 'duplicate',
      uploadId: UPLOAD_ID,
      externalId: EXTERNAL_ID,
    });
  });

  it.each([
    ['stato sconosciuto', { uploadId: UPLOAD_ID, externalId: EXTERNAL_ID, status: 'queued' }],
    ['campo mancante', { uploadId: UPLOAD_ID, status: 'accepted' }],
    ['tipo sbagliato', { uploadId: 5, externalId: EXTERNAL_ID, status: 'accepted' }],
    ['externalId vuoto', { uploadId: UPLOAD_ID, externalId: '', status: 'accepted' }],
    ['JSON non oggetto', ['accepted']],
    ['null', 'null'],
    ['testo non JSON', 'OK'],
    ['corpo vuoto', ''],
  ])('200 con corpo inatteso (%s) è "unexpected", non un crash', async (_name, body) => {
    const promise = uploadFile(file, { apiBase: API });
    FakeXhr.last().respond(200, body);
    await expect(promise).resolves.toEqual({ ok: false, reason: 'unexpected', httpStatus: 200 });
  });

  it('un 2xx diverso da 200/202 (es. 204) è "unexpected"', async () => {
    const promise = uploadFile(file, { apiBase: API });
    FakeXhr.last().respond(204, '');
    await expect(promise).resolves.toEqual({ ok: false, reason: 'unexpected', httpStatus: 204 });
  });
});

describe('uploadFile: errori ProblemDetails', () => {
  const codes = [
    ['invalid-file-field', 400],
    ['malformed-request', 400],
    ['empty-file', 400],
    ['unsupported-file-type', 415],
    ['file-too-large', 413],
    ['invalid-activity-file', 422],
    ['too-many-points', 422],
    ['parse-timeout', 422],
    ['event-too-large', 422],
    ['internal-error', 500],
  ] as const;

  it.each(codes)('%s (%i) è riconosciuto per type', async (code, httpStatus) => {
    const promise = uploadFile(file, { apiBase: API });
    FakeXhr.last().respond(httpStatus, {
      type: `urn:trimetric:problem:${code}`,
      title: 'Titolo del server',
      status: httpStatus,
      detail: 'Dettaglio in italiano del server',
    });
    await expect(promise).resolves.toEqual({ ok: false, reason: 'problem', code, httpStatus });
  });

  it('un type sconosciuto diventa un errore HTTP per stato (nessun testo del server)', async () => {
    const promise = uploadFile(file, { apiBase: API });
    FakeXhr.last().respond(422, { type: 'urn:trimetric:problem:nuovo-codice', detail: 'segreto' });
    await expect(promise).resolves.toEqual({ ok: false, reason: 'http', httpStatus: 422 });
  });

  it('il ProblemDetails del gateway (type RFC, senza urn trimetric) è un errore HTTP 413', async () => {
    const promise = uploadFile(file, { apiBase: API });
    FakeXhr.last().respond(413, {
      type: 'https://tools.ietf.org/html/rfc9110#section-15.5.14',
      title: 'Richiesta troppo grande.',
      status: 413,
    });
    await expect(promise).resolves.toEqual({ ok: false, reason: 'http', httpStatus: 413 });
  });

  it.each([
    [502, ''],
    [504, ''],
    [400, ''],
    [404, ''],
    [503, '<html>Service Unavailable</html>'],
    [500, 'null'],
    [422, '{"type": 5}'],
  ])('%i con corpo vuoto o non utilizzabile (%j) è un errore HTTP per stato', async (httpStatus, body) => {
    const promise = uploadFile(file, { apiBase: API });
    FakeXhr.last().respond(httpStatus, body);
    await expect(promise).resolves.toEqual({ ok: false, reason: 'http', httpStatus });
  });

  it('il risultato non contiene mai il detail o il title del server', async () => {
    const promise = uploadFile(file, { apiBase: API });
    FakeXhr.last().respond(422, {
      type: 'urn:trimetric:problem:invalid-activity-file',
      title: 'TITOLO-SEGRETO',
      detail: 'DETTAGLIO-SEGRETO',
    });
    expect(JSON.stringify(await promise)).not.toMatch(/SEGRETO/);
  });
});

describe('uploadFile: rete e annullamento', () => {
  it('un errore di rete è "network"', async () => {
    const promise = uploadFile(file, { apiBase: API });
    FakeXhr.last().failNetwork();
    await expect(promise).resolves.toEqual({ ok: false, reason: 'network' });
  });

  it('il segnale già annullato non fa partire nessuna richiesta', async () => {
    const controller = new AbortController();
    controller.abort();
    await expect(uploadFile(file, { apiBase: API, signal: controller.signal })).resolves.toEqual({
      ok: false,
      reason: 'aborted',
    });
    expect(FakeXhr.instances).toHaveLength(0);
  });

  it("annullare il segnale durante l'invio interrompe la richiesta e dà 'aborted'", async () => {
    const controller = new AbortController();
    const promise = uploadFile(file, { apiBase: API, signal: controller.signal });
    controller.abort();
    await expect(promise).resolves.toEqual({ ok: false, reason: 'aborted' });
    expect(FakeXhr.last().aborted).toBe(true);
  });

  it('dopo la fine toglie l\'ascoltatore dal segnale: un abort tardivo non chiama xhr.abort', async () => {
    const controller = new AbortController();
    const removeSpy = vi.spyOn(controller.signal, 'removeEventListener');
    const abortSpy = vi.spyOn(FakeXhr.prototype, 'abort');
    const promise = uploadFile(file, { apiBase: API, signal: controller.signal });
    FakeXhr.last().respond(202, { uploadId: UPLOAD_ID, externalId: EXTERNAL_ID, status: 'accepted' });
    await promise;
    expect(removeSpy).toHaveBeenCalledWith('abort', expect.any(Function));
    controller.abort();
    expect(abortSpy).not.toHaveBeenCalled();
  });
});

describe('uploadFile: avanzamento', () => {
  it('riporta la percentuale intera da 0 a 100 e non oltre', () => {
    const onProgress = vi.fn();
    void uploadFile(file, { apiBase: API, onProgress });
    const xhr = FakeXhr.last();
    xhr.emitProgress(0, 200);
    xhr.emitProgress(50, 200);
    xhr.emitProgress(133, 200);
    xhr.emitProgress(200, 200);
    xhr.emitProgress(250, 200);
    expect(onProgress.mock.calls.map(([p]) => p)).toEqual([0, 25, 67, 100, 100]);
  });

  it('riporta null (avanzamento indeterminato) se la lunghezza non è calcolabile o il totale è zero', () => {
    const onProgress = vi.fn();
    void uploadFile(file, { apiBase: API, onProgress });
    FakeXhr.last().emitIndeterminateProgress(100);
    FakeXhr.last().emitProgress(0, 0);
    expect(onProgress.mock.calls.map(([p]) => p)).toEqual([null, null]);
  });

  it("non riporta avanzamento dopo l'annullamento", () => {
    const onProgress = vi.fn();
    const controller = new AbortController();
    void uploadFile(file, { apiBase: API, onProgress, signal: controller.signal });
    controller.abort();
    FakeXhr.last().emitProgress(10, 100);
    expect(onProgress).not.toHaveBeenCalled();
  });
});
