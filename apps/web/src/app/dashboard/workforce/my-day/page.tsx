'use client';

import { Suspense } from 'react';
import { useSearchParams } from 'next/navigation';
import { useQuery } from '@tanstack/react-query';
import { Sun } from 'lucide-react';
import { api } from '@/lib/api';
import { WorkforceNav } from '@/components/WorkforceCapacity';
import { MyDayView } from '@/components/WorkforceMyDay';

/**
 * My Day: what I should work on today, what is running now, what is next, what was planned against
 * what was actually done, and what of mine is still unscheduled. The clock is attached to a piece of
 * work, never to the day: this is work execution, not attendance. Reads the query string (a manager
 * opens someone's day from Team today; a notification opens a date), hence Suspense.
 */
export default function MyDayPage() {
  return (
    <Suspense fallback={<div className="h-64 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}>
      <MyDay />
    </Suspense>
  );
}

function MyDay() {
  const query = useSearchParams();
  const date = /^\d{4}-\d{2}-\d{2}$/.test(query.get('date') ?? '') ? query.get('date') : null;
  const person = query.get('person');
  const { data: me, isLoading } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  return (
    <div className="space-y-4">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-semibold tracking-tight"><Sun size={22} className="text-brand" aria-hidden="true" /> My day</h1>
        <p className="max-w-prose text-sm text-[var(--muted)]">What to work on, what is running, and what was planned against what was done. Start a clock on a piece of work; stopping it logs the time on the ticket.</p>
      </div>
      <WorkforceNav />
      {isLoading && <p className="text-sm text-[var(--muted)]">Loading…</p>}
      {me && !me.userId && <p className="text-sm text-[var(--muted)]">This sign-in is not a staff account.</p>}
      {me?.userId && <MyDayView viewerId={me.userId} appUserId={person && person !== me.userId ? person : null} initialDate={date} />}
    </div>
  );
}
