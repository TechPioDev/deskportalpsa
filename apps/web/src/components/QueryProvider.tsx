'use client';

import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useState } from 'react';

export function QueryProvider({ children }: { children: React.ReactNode }) {
  const [client] = useState(
    () =>
      new QueryClient({
        defaultOptions: {
          // networkMode 'always': React Query's default pauses every request while the browser
          // reports itself offline - silently, with no error. Chrome's online flag flaps on some
          // office networks (VPN and virtual adapters), and a status change made during a flap
          // simply never left the browser: the badge reset and nothing was shown. Attempting the
          // request regardless means a real outage surfaces as an error the page can display.
          queries: { retry: false, staleTime: 15_000, refetchOnWindowFocus: false, networkMode: 'always' },
          mutations: { networkMode: 'always' },
        },
      }),
  );
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}
