import { describe, expect, it, vi } from 'vitest';
import { activitiesBaseUrl, DEFAULT_API_BASE, ingestionBaseUrl, normalizeApiBase } from './config';

describe('normalizeApiBase', () => {
  it('usa /api se la variabile manca o è vuota', () => {
    expect(DEFAULT_API_BASE).toBe('/api');
    expect(normalizeApiBase(undefined)).toBe('/api');
    expect(normalizeApiBase('')).toBe('/api');
    expect(normalizeApiBase('   ')).toBe('/api');
  });

  it('toglie le barre finali', () => {
    expect(normalizeApiBase('/api/')).toBe('/api');
    expect(normalizeApiBase('https://gateway.example.test/api//')).toBe('https://gateway.example.test/api');
  });

  it('toglie gli spazi attorno al valore', () => {
    expect(normalizeApiBase('  /api  ')).toBe('/api');
  });

  it('accetta una base assoluta (guscio nativo)', () => {
    expect(normalizeApiBase('https://gateway.example.test/api')).toBe('https://gateway.example.test/api');
  });

  it('"/" significa API servita dalla radice: la base diventa vuota', () => {
    expect(normalizeApiBase('/')).toBe('');
  });
});

describe('basi dei due client', () => {
  it('activities usa la base com\'è (i percorsi OpenAPI sono /activities e /activities/{id})', () => {
    expect(activitiesBaseUrl('/api')).toBe('/api');
  });

  it('ingestion aggiunge /ingestion (il percorso OpenAPI è /uploads)', () => {
    expect(ingestionBaseUrl('/api')).toBe('/api/ingestion');
    expect(ingestionBaseUrl('https://gateway.example.test/api')).toBe('https://gateway.example.test/api/ingestion');
  });
});

describe('API_BASE letta da VITE_API_BASE_URL', () => {
  it('senza variabile vale /api', async () => {
    vi.stubEnv('VITE_API_BASE_URL', '');
    vi.resetModules();
    const config = await import('./config');
    expect(config.API_BASE).toBe('/api');
    expect(config.ACTIVITIES_BASE_URL).toBe('/api');
    expect(config.INGESTION_BASE_URL).toBe('/api/ingestion');
  });

  it('cambia con la variabile, senza barra finale', async () => {
    vi.stubEnv('VITE_API_BASE_URL', 'https://gateway.example.test/v1/');
    vi.resetModules();
    const config = await import('./config');
    expect(config.API_BASE).toBe('https://gateway.example.test/v1');
    expect(config.ACTIVITIES_BASE_URL).toBe('https://gateway.example.test/v1');
    expect(config.INGESTION_BASE_URL).toBe('https://gateway.example.test/v1/ingestion');
  });
});
