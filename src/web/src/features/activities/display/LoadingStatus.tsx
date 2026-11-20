import { LoaderCircle } from 'lucide-react';

/** Stato di caricamento annunciato agli screen reader (`role="status"`), con icona che gira (ferma se si riduce il movimento). */
export function LoadingStatus({ label }: { label: string }) {
  return (
    <div role="status" aria-live="polite" className="flex items-center gap-3 py-8 text-muted">
      <LoaderCircle aria-hidden="true" className="size-5 shrink-0 animate-spin motion-reduce:animate-none" />
      <span>{label}</span>
    </div>
  );
}
