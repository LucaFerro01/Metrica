import { QueryClient } from '@tanstack/react-query';

export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        // Gli errori di rete si riprovano una volta; il polling dell'elenco (T18) è esplicito, non qui.
        retry: 1,
        refetchOnWindowFocus: false,
      },
    },
  });
}
