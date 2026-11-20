import type { ReactNode } from 'react';

interface PageHeaderProps {
  title: string;
  description?: ReactNode;
}

/** Titolo di pagina (h1) con descrizione facoltativa. */
export function PageHeader({ title, description }: PageHeaderProps) {
  return (
    <div className="mb-6">
      <h1 className="text-2xl font-semibold tracking-tight md:text-3xl">{title}</h1>
      {description ? <p className="mt-2 text-muted">{description}</p> : null}
    </div>
  );
}
