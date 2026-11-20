import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterEach, beforeEach, vi } from 'vitest';

// jsdom non implementa `scrollTo` e TanStack Router lo chiama a ogni navigazione (ripristino dello scorrimento):
// senza questo finto stampa "Not implemented" a ogni test.
beforeEach(() => {
  vi.stubGlobal('scrollTo', () => undefined);
});

// Senza `globals` Testing Library non registra da sola la pulizia del DOM tra un test e l'altro.
afterEach(() => {
  cleanup();
});
