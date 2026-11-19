import { describe, expect, it } from 'vitest';
import {
  formatDate,
  formatDateTime,
  formatDistance,
  formatDuration,
  formatElevation,
  formatMetric,
} from './format';

// Spazio non separabile tra numero e unità: la formattazione non deve andare a capo in mezzo.
const NBSP = ' ';

describe('formatDistance', () => {
  it.each([null, undefined])('restituisce null per %s', (value) => {
    expect(formatDistance(value, 'it')).toBeNull();
  });

  it('restituisce null per valori non validi (non finiti o negativi)', () => {
    expect(formatDistance(Number.NaN, 'it')).toBeNull();
    expect(formatDistance(Number.POSITIVE_INFINITY, 'it')).toBeNull();
    expect(formatDistance(-1, 'it')).toBeNull();
  });

  it('converte i metri in chilometri con due decimali, in italiano', () => {
    expect(formatDistance(12345, 'it')).toBe(`12,35${NBSP}km`);
    expect(formatDistance(1000, 'it')).toBe(`1,00${NBSP}km`);
  });

  it('converte i metri in chilometri con due decimali, in inglese', () => {
    expect(formatDistance(12345, 'en')).toBe(`12.35${NBSP}km`);
    expect(formatDistance(1000, 'en')).toBe(`1.00${NBSP}km`);
  });

  it('sotto 1 km mostra i metri interi', () => {
    expect(formatDistance(0, 'it')).toBe(`0${NBSP}m`);
    expect(formatDistance(850, 'en')).toBe(`850${NBSP}m`);
    expect(formatDistance(999.4, 'it')).toBe(`999${NBSP}m`);
  });

  it('non mostra mai "1000 m": un valore che arrotonda a 1000 m diventa 1,00 km', () => {
    expect(formatDistance(999.5, 'it')).toBe(`1,00${NBSP}km`);
    expect(formatDistance(999.9, 'en')).toBe(`1.00${NBSP}km`);
  });

  it('arrotonda al centesimo di chilometro', () => {
    expect(formatDistance(1004, 'en')).toBe(`1.00${NBSP}km`);
    expect(formatDistance(1006, 'en')).toBe(`1.01${NBSP}km`);
  });

  it('gestisce valori grandi con il separatore delle migliaia della lingua', () => {
    expect(formatDistance(250000, 'it')).toBe(`250,00${NBSP}km`);
    expect(formatDistance(250000, 'en')).toBe(`250.00${NBSP}km`);
    expect(formatDistance(12345678, 'en')).toBe(`12,345.68${NBSP}km`);
    expect(formatDistance(12345678, 'it')).toBe(`12.345,68${NBSP}km`);
  });
});

describe('formatDuration', () => {
  it.each([null, undefined])('restituisce null per %s', (value) => {
    expect(formatDuration(value)).toBeNull();
  });

  it('restituisce null per valori non validi', () => {
    expect(formatDuration(Number.NaN)).toBeNull();
    expect(formatDuration(Number.NEGATIVE_INFINITY)).toBeNull();
    expect(formatDuration(-1)).toBeNull();
  });

  it('usa m:ss sotto l\'ora', () => {
    expect(formatDuration(0)).toBe('0:00');
    expect(formatDuration(5)).toBe('0:05');
    expect(formatDuration(59)).toBe('0:59');
    expect(formatDuration(60)).toBe('1:00');
    expect(formatDuration(3599)).toBe('59:59');
  });

  it('usa h:mm:ss da un\'ora in su', () => {
    expect(formatDuration(3600)).toBe('1:00:00');
    expect(formatDuration(3725)).toBe('1:02:05');
    expect(formatDuration(36000)).toBe('10:00:00');
  });

  it('gestisce durate molto lunghe senza troncare le ore', () => {
    expect(formatDuration(100000)).toBe('27:46:40');
    expect(formatDuration(360000)).toBe('100:00:00');
  });

  it('arrotonda ai secondi interi prima di scomporre', () => {
    expect(formatDuration(59.4)).toBe('0:59');
    expect(formatDuration(59.5)).toBe('1:00');
    expect(formatDuration(3599.6)).toBe('1:00:00');
  });
});

describe('formatElevation', () => {
  it.each([null, undefined])('restituisce null per %s', (value) => {
    expect(formatElevation(value, 'it')).toBeNull();
  });

  it('restituisce null per valori non finiti', () => {
    expect(formatElevation(Number.NaN, 'it')).toBeNull();
    expect(formatElevation(Number.POSITIVE_INFINITY, 'en')).toBeNull();
  });

  it('mostra metri interi', () => {
    expect(formatElevation(0, 'it')).toBe(`0${NBSP}m`);
    expect(formatElevation(12.4, 'it')).toBe(`12${NBSP}m`);
    expect(formatElevation(12.5, 'en')).toBe(`13${NBSP}m`);
  });

  it('non mostra uno zero negativo', () => {
    expect(formatElevation(-0.4, 'en')).toBe(`0${NBSP}m`);
  });

  it('usa il separatore delle migliaia della lingua per valori grandi', () => {
    expect(formatElevation(8848, 'en')).toBe(`8,848${NBSP}m`);
    expect(formatElevation(88480, 'it')).toBe(`88.480${NBSP}m`);
  });
});

describe('formatMetric', () => {
  it('restituisce null per assenti e non validi', () => {
    expect(formatMetric(null, 'bpm', 'it')).toBeNull();
    expect(formatMetric(undefined, 'W', 'en')).toBeNull();
    expect(formatMetric(Number.NaN, 'rpm', 'en')).toBeNull();
  });

  it('mostra interi con unità', () => {
    expect(formatMetric(152, 'bpm', 'it')).toBe(`152${NBSP}bpm`);
    expect(formatMetric(250, 'W', 'en')).toBe(`250${NBSP}W`);
    expect(formatMetric(88, 'rpm', 'it')).toBe(`88${NBSP}rpm`);
  });

  it('arrotonda e gestisce lo zero', () => {
    expect(formatMetric(0, 'W', 'en')).toBe(`0${NBSP}W`);
    expect(formatMetric(152.6, 'bpm', 'en')).toBe(`153${NBSP}bpm`);
    expect(formatMetric(152.4, 'bpm', 'en')).toBe(`152${NBSP}bpm`);
  });

  it('usa il separatore delle migliaia per valori grandi', () => {
    expect(formatMetric(1234, 'W', 'en')).toBe(`1,234${NBSP}W`);
    expect(formatMetric(12345, 'W', 'it')).toBe(`12.345${NBSP}W`);
  });
});

describe('formatDateTime e formatDate', () => {
  // I test costruiscono le date con i componenti locali: il risultato atteso non dipende
  // dal fuso orario della macchina (la formattazione usa il fuso del browser).
  const local = new Date(2026, 10, 19, 14, 30, 0);

  it.each([null, undefined])('restituiscono null per %s', (value) => {
    expect(formatDateTime(value, 'it')).toBeNull();
    expect(formatDate(value, 'it')).toBeNull();
  });

  it('restituiscono null per date non valide', () => {
    expect(formatDateTime('non una data', 'it')).toBeNull();
    expect(formatDate(new Date(Number.NaN), 'en')).toBeNull();
  });

  it('formatta data e ora in italiano', () => {
    const text = formatDateTime(local, 'it');
    expect(text).toContain('19');
    expect(text).toContain('2026');
    expect(text).toContain('14:30');
    expect(text?.toLowerCase()).toContain('nov');
  });

  it('formatta data e ora in inglese (12 ore)', () => {
    const text = formatDateTime(local, 'en');
    expect(text).toContain('Nov');
    expect(text).toContain('19');
    expect(text).toContain('2026');
    expect(text).toMatch(/2:30\s?PM/);
  });

  it('formatta solo la data, senza ora', () => {
    const text = formatDate(local, 'it');
    expect(text).toContain('19');
    expect(text).not.toContain('14:30');
  });

  it('accetta stringhe ISO e timestamp, nello stesso istante', () => {
    expect(formatDateTime(local.toISOString(), 'en')).toBe(formatDateTime(local, 'en'));
    expect(formatDateTime(local.getTime(), 'en')).toBe(formatDateTime(local, 'en'));
  });

  it('usa il fuso del browser: due stringhe con offset diverso ma stesso istante danno lo stesso testo', () => {
    expect(formatDateTime('2026-11-19T10:00:00+01:00', 'en')).toBe(
      formatDateTime('2026-11-19T09:00:00Z', 'en'),
    );
  });
});
