import type { ReactNode } from 'react';
import { cn } from './cn';

export interface SegmentedOption<T extends string> {
  value: T;
  /** Nome accessibile (già tradotto) del pulsante. */
  label: string;
  /** Contenuto visibile: icona o testo breve. */
  content: ReactNode;
}

interface SegmentedControlProps<T extends string> {
  /** Nome accessibile (già tradotto) del gruppo. */
  label: string;
  value: T;
  options: readonly SegmentedOption<T>[];
  onChange: (value: T) => void;
}

/**
 * Gruppo di pulsanti mutuamente esclusivi (`role="group"` + `aria-pressed`), usato per l'interruttore
 * del tema e per il selettore di lingua. Ogni pulsante ha un bersaglio di tocco di almeno 44 px.
 */
export function SegmentedControl<T extends string>({ label, value, options, onChange }: SegmentedControlProps<T>) {
  return (
    <div role="group" aria-label={label} className="inline-flex shrink-0 rounded-lg border border-border bg-background p-0.5">
      {options.map((option) => {
        const selected = option.value === value;
        return (
          <button
            key={option.value}
            type="button"
            aria-pressed={selected}
            aria-label={option.label}
            title={option.label}
            onClick={() => onChange(option.value)}
            className={cn(
              'inline-flex min-h-11 min-w-11 items-center justify-center rounded-md px-2 text-sm font-medium transition-colors',
              'focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-accent',
              selected ? 'bg-accent text-accent-foreground' : 'text-muted hover:text-foreground',
            )}
          >
            {option.content}
          </button>
        );
      })}
    </div>
  );
}
