import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

const html = readFileSync(path.resolve(import.meta.dirname, '../../index.html'), 'utf8');
const doc = new DOMParser().parseFromString(html, 'text/html');

describe('index.html', () => {
  it('dichiara il viewport con viewport-fit=cover (margini sicuri del guscio nativo)', () => {
    const content = doc.querySelector('meta[name="viewport"]')?.getAttribute('content') ?? '';
    const parts = content.split(',').map((part) => part.trim());
    expect(parts).toContain('viewport-fit=cover');
    expect(parts).toContain('width=device-width');
    expect(parts).toContain('initial-scale=1');
  });

  it('non impedisce lo zoom (accessibilità)', () => {
    const content = doc.querySelector('meta[name="viewport"]')?.getAttribute('content') ?? '';
    expect(content).not.toMatch(/user-scalable\s*=\s*(no|0)/);
    expect(content).not.toMatch(/maximum-scale/);
  });

  it('ha la lingua predefinita italiana e un contenitore per l\'app', () => {
    expect(doc.documentElement.getAttribute('lang')).toBe('it');
    expect(doc.getElementById('root')).not.toBeNull();
  });

  it('non collega manifest PWA né registra service worker (DEC-015)', () => {
    expect(doc.querySelector('link[rel="manifest"]')).toBeNull();
    expect(html).not.toMatch(/serviceWorker/);
  });

  it('carica l\'app con un modulo da /src/main.tsx', () => {
    expect(doc.querySelector('script[type="module"][src="/src/main.tsx"]')).not.toBeNull();
  });
});
