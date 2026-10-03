'use client';

import { useQuery } from '@tanstack/react-query';
import { Users } from 'lucide-react';
import { api } from '@/lib/api';
import { WorkforceNav } from '@/components/WorkforceCapacity';
import { TeamTodayView } from '@/components/WorkforceMyDay';

/** Team today: who is working on what, planned against actual, for the people the viewer may see. Operational, not surveillance. */
export default function TeamTodayPage() {
  const { data: me, isLoading } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  return (
    <div className="space-y-4">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-semibold tracking-tight"><Users size={22} className="text-brand" aria-hidden="true" /> Team today</h1>
        <p className="max-w-prose text-sm text-[var(--muted)]">Who is on what right now, and how much of today&rsquo;s plan is done. Work facts only: no attendance, no ranking.</p>
      </div>
      <WorkforceNav />
      {isLoading && <p className="text-sm text-[var(--muted)]">Loading…</p>}
      {me && !me.userId && <p className="text-sm text-[var(--muted)]">This sign-in is not a staff account.</p>}
      {me?.userId && <TeamTodayView viewerId={me.userId} />}
    </div>
  );
}
