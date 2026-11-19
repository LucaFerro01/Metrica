import { renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { I18nextProvider } from 'react-i18next';
import { afterEach, describe, expect, it } from 'vitest';
import { i18n, initI18n } from './index';
import { useFormatters } from './useFormatters';

const wrapper = ({ children }: { children: ReactNode }) => <I18nextProvider i18n={i18n}>{children}</I18nextProvider>;

afterEach(async () => {
  await i18n.changeLanguage('it');
});

describe('useFormatters', () => {
  it('formatta nella lingua corrente e la segue quando cambia', async () => {
    await initI18n();
    await i18n.changeLanguage('it');
    const { result, rerender } = renderHook(() => useFormatters(), { wrapper });
    expect(result.current.distance(12345)).toBe('12,35 km');

    await i18n.changeLanguage('en');
    rerender();
    expect(result.current.distance(12345)).toBe('12.35 km');
    expect(result.current.locale).toBe('en');
  });

  it('restituisce null per i valori assenti (la UI mostra "non disponibile")', async () => {
    await initI18n();
    const { result } = renderHook(() => useFormatters(), { wrapper });
    expect(result.current.distance(null)).toBeNull();
    expect(result.current.duration(undefined)).toBeNull();
    expect(result.current.elevation(null)).toBeNull();
    expect(result.current.metric(undefined, 'bpm')).toBeNull();
    expect(result.current.dateTime(null)).toBeNull();
  });
});
