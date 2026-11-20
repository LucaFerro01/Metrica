import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import {
  formatDate,
  formatDateTime,
  formatDistance,
  formatElevation,
  formatFileSize,
  formatDuration,
  formatMetric,
  type MetricUnit,
} from '@/shared/lib/format';

/**
 * Le funzioni di `shared/lib/format` già legate alla lingua corrente dell'interfaccia.
 * Restituiscono `null` per i valori assenti: la UI mostra `t('common.unavailable')`.
 */
export function useFormatters() {
  const { i18n } = useTranslation();
  const locale = i18n.resolvedLanguage ?? 'it';
  return useMemo(
    () => ({
      locale,
      distance: (meters: number | null | undefined) => formatDistance(meters, locale),
      duration: (seconds: number | null | undefined) => formatDuration(seconds),
      elevation: (meters: number | null | undefined) => formatElevation(meters, locale),
      fileSize: (bytes: number | null | undefined) => formatFileSize(bytes, locale),
      metric: (value: number | null | undefined, unit: MetricUnit) => formatMetric(value, unit, locale),
      dateTime: (value: string | number | Date | null | undefined) => formatDateTime(value, locale),
      date: (value: string | number | Date | null | undefined) => formatDate(value, locale),
    }),
    [locale],
  );
}
