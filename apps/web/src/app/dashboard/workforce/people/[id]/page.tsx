'use client';

import { Suspense, use, useState } from 'react';
import Link from 'next/link';
import { useSearchParams } from 'next/navigation';
import { useQuery } from '@tanstack/react-query';
import { ArrowLeft } from 'lucide-react';
import { api } from '@/lib/api';
import { StaffSkillsEditor, WorkScheduleEditor } from '@/components/Workforce';
import { CapacityPanel } from '@/components/WorkforceCapacity';
import { PlanAgenda } from '@/components/WorkforcePlan';

const TABS = [['plan', 'Plan'], ['schedule', 'Work schedule'], ['availability', 'Availability'], ['skills', 'Skills']] as const;
type Tab = typeof TABS[number][0];

/**
 * One person's working schedule, availability and skills: editable by whoever manages them,
 * read-only to others who may see them. Wrapped in Suspense because the page reads the query string
 * (a link from team capacity or the technician search opens it on a date).
 */
export default function WorkforcePersonPage({ params }: { params: Promise<{ id: string }> }) {
  return (
    <Suspense fallback={<div className="mx-auto h-64 max-w-3xl animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}>
      <WorkforcePerson params={params} />
    </Suspense>
  );
}

function WorkforcePerson({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params);
  const query = useSearchParams();
  const asked = query.get('tab');
  const date = /^\d{4}-\d{2}-\d{2}$/.test(query.get('date') ?? '') ? query.get('date') : null;
  const [tab, setTab] = useState<Tab>(TABS.some(([key]) => key === asked) ? (asked as Tab) : 'plan');
  const { data: me } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  const { data: schedule } = useQuery({ queryKey: ['work-schedule', id], queryFn: () => api.workSchedule(id), retry: false });

  return (
    <div className="mx-auto max-w-4xl space-y-5">
      <Link href="/dashboard/workforce" className="inline-flex items-center gap-1.5 text-sm text-[var(--muted)] hover:text-[var(--fg)]">
        <ArrowLeft size={16} /> Workforce
      </Link>
      <h1 className="text-2xl font-semibold tracking-tight">{schedule?.displayName ?? ' '}</h1>
      <div role="tablist" className="flex gap-1 border-b border-[var(--border)]">
        {TABS.map(([key, label]) => (
          <button key={key} role="tab" aria-selected={tab === key} onClick={() => setTab(key)}
            className={`border-b-2 px-3 py-2 text-sm font-medium ${tab === key ? 'border-brand text-[var(--fg)]' : 'border-transparent text-[var(--muted)] hover:text-[var(--fg)]'}`}>
            {label}
          </button>
        ))}
      </div>
      {tab === 'plan' && <PlanAgenda userId={id} viewerId={me?.userId ?? null} initialDate={date} />}
      {tab === 'schedule' && <WorkScheduleEditor userId={id} />}
      {tab === 'availability' && <CapacityPanel userId={id} initialDate={date} />}
      {tab === 'skills' && <StaffSkillsEditor userId={id} canManage={!!schedule?.canManage} />}
    </div>
  );
}
