import type { ButtonHTMLAttributes } from 'react';
import { cn } from './cn';

type Variant = 'primary' | 'secondary' | 'ghost';

const variants: Record<Variant, string> = {
  primary: 'bg-accent text-accent-foreground hover:opacity-90',
  secondary: 'border border-border bg-surface text-foreground hover:bg-background',
  ghost: 'text-foreground hover:bg-surface',
};

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant;
}

/** Pulsante di base. Bersaglio di tocco di almeno 44 px (min-h-11 = 2,75 rem). */
export function Button({ variant = 'primary', className, type = 'button', ...rest }: ButtonProps) {
  return (
    <button
      type={type}
      className={cn(
        'inline-flex min-h-11 min-w-11 items-center justify-center gap-2 rounded-lg px-4 text-sm font-medium',
        'transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent',
        'disabled:pointer-events-none disabled:opacity-50',
        variants[variant],
        className,
      )}
      {...rest}
    />
  );
}
