'use client';

import { useQuery } from '@tanstack/react-query';
import { Gauge } from 'lucide-react';
import { api } from '@/lib/api';
import { CapacityPanel, WorkforceNav } from '@/components/WorkforceCapacity';

/** The signed-in person's own capacity: today's sums, the week, and exactly when they are free. */
export default function MyCapacityPage() {
  const { data: me, isLoading } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });

  return (
    <div className="space-y-5">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-semibold tracking-tight"><Gauge size={22} className="text-brand" aria-hidden="true" /> My capacity</h1>
        <p className="max-w-prose text-sm text-[var(--muted)]">Your working time, what is planned in it, and your free windows.</p>
      </div>
      <WorkforceNav />
      {isLoading && <p className="text-sm text-[var(--muted)]">Loading…</p>}
      {me && !me.userId && <p className="text-sm text-[var(--muted)]">This sign-in is not a staff account, so it has no working schedule or capacity.</p>}
      {me?.userId && <CapacityPanel userId={me.userId} />}
    </div>
  );
}
