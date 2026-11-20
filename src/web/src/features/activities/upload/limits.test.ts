import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { MAX_UPLOAD_BYTES, NETWORK_ERROR_LARGE_FILE_BYTES, validateUploadFile } from './limits';

const MIB = 1024 * 1024;

describe('costanti dei limiti', () => {
  it('il limite è esattamente 32 MiB = 33.554.432 byte (come Upload:MaxBytes di ingestion)', () => {
    expect(MAX_UPLOAD_BYTES).toBe(33_554_432);
    expect(MAX_UPLOAD_BYTES).toBe(32 * MIB);
  });

  it('la soglia per il messaggio di rete sui file grandi è 8 MiB', () => {
    expect(NETWORK_ERROR_LARGE_FILE_BYTES).toBe(8 * MIB);
  });
});

/** Radice del repository: si risale da `process.cwd()` fino alla cartella che contiene `Metrica.slnx`. */
function findRepoRoot(): string {
  let dir = process.cwd();
  for (let level = 0; level < 10; level += 1) {
    if (existsSync(join(dir, 'Metrica.slnx'))) return dir;
    const parent = dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  throw new Error(`Radice del repository (Metrica.slnx) non trovata risalendo da ${process.cwd()}`);
}

describe('coerenza con il backend', () => {
  const APPSETTINGS = join(
    findRepoRoot(),
    'src',
    'services',
    'ingestion',
    'src',
    'Metrica.Ingestion',
    'appsettings.json',
  );

  it('MAX_UPLOAD_BYTES coincide con Upload:MaxBytes di appsettings.json di ingestion', () => {
    expect(existsSync(APPSETTINGS), `file non trovato: ${APPSETTINGS}`).toBe(true);
    const settings = JSON.parse(readFileSync(APPSETTINGS, 'utf8')) as { Upload?: { MaxBytes?: unknown } };
    expect(settings.Upload?.MaxBytes, 'chiave Upload:MaxBytes assente in appsettings.json').toBeDefined();
    expect(MAX_UPLOAD_BYTES).toBe(settings.Upload?.MaxBytes);
  });
});

describe('validateUploadFile', () => {
  it.each(['corsa.gpx', 'giro.fit', 'CORSA.GPX', 'Giro.Fit', 'archivio.2024.05.gpx', 'a b c.FIT'])(
    'accetta %s',
    (name) => {
      expect(validateUploadFile({ name, size: 1000 })).toBeNull();
    },
  );

  it.each(['note.txt', 'corsa.gpx.txt', 'corsa', 'corsa.tcx', 'corsa.gpxx', 'gpx', 'corsa.fit.exe', ''])(
    'rifiuta %j per estensione non ammessa',
    (name) => {
      expect(validateUploadFile({ name, size: 1000 })).toBe('unsupported-type');
    },
  );

  it('rifiuta un file vuoto (0 byte)', () => {
    expect(validateUploadFile({ name: 'corsa.gpx', size: 0 })).toBe('empty');
  });

  it('accetta 1 byte; una dimensione negativa o non numerica non è mai accettata', () => {
    expect(validateUploadFile({ name: 'corsa.gpx', size: 1 })).toBeNull();
    expect(validateUploadFile({ name: 'corsa.gpx', size: -1 })).toBe('empty');
    expect(validateUploadFile({ name: 'corsa.gpx', size: Number.NaN })).toBe('empty');
  });

  it('accetta esattamente 32 MiB', () => {
    expect(validateUploadFile({ name: 'corsa.gpx', size: 33_554_432 })).toBeNull();
  });

  it('rifiuta 32 MiB + 1 byte', () => {
    expect(validateUploadFile({ name: 'corsa.gpx', size: 33_554_433 })).toBe('too-large');
  });

  it('l\'estensione ha la precedenza: un .txt vuoto o enorme è "tipo non ammesso"', () => {
    expect(validateUploadFile({ name: 'a.txt', size: 0 })).toBe('unsupported-type');
    expect(validateUploadFile({ name: 'a.txt', size: 40 * MIB })).toBe('unsupported-type');
  });
});
