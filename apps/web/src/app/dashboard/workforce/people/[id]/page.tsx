'use client';

import { use, useState } from 'react';
import Link from 'next/link';
import { useQuery } from '@tanstack/react-query';
import { ArrowLeft } from 'lucide-react';
import { api } from '@/lib/api';
import { StaffSkillsEditor, WorkScheduleEditor } from '@/components/Workforce';

/** One person's working schedule and skills: editable by a workforce manager, read-only to others who may see them. */
export default function WorkforcePersonPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params);
  const [tab, setTab] = useState<'schedule' | 'skills'>('schedule');
  const { data: schedule } = useQuery({ queryKey: ['work-schedule', id], queryFn: () => api.workSchedule(id), retry: false });

  return (
    <div className="mx-auto max-w-3xl space-y-5">
      <Link href="/dashboard/workforce" className="inline-flex items-center gap-1.5 text-sm text-[var(--muted)] hover:text-[var(--fg)]">
        <ArrowLeft size={16} /> Workforce
      </Link>
      <h1 className="text-2xl font-semibold tracking-tight">{schedule?.displayName ?? ' '}</h1>
      <div role="tablist" className="flex gap-1 border-b border-[var(--border)]">
        {(['schedule', 'skills'] as const).map((t) => (
          <button key={t} role="tab" aria-selected={tab === t} onClick={() => setTab(t)}
            className={`border-b-2 px-3 py-2 text-sm font-medium ${tab === t ? 'border-brand text-[var(--fg)]' : 'border-transparent text-[var(--muted)] hover:text-[var(--fg)]'}`}>
            {t === 'schedule' ? 'Work schedule' : 'Skills'}
          </button>
        ))}
      </div>
      {tab === 'schedule' ? <WorkScheduleEditor userId={id} /> : <StaffSkillsEditor userId={id} canManage={!!schedule?.canManage} />}
    </div>
  );
}
