'use client';

import { useMemo, useState } from 'react';
import Link from 'next/link';
import { useQuery } from '@tanstack/react-query';
import { CalendarClock, Tags } from 'lucide-react';
import { api } from '@/lib/api';
import { hours } from '@/components/Workforce';

/**
 * Who works when, and what they know: the people the viewer may see, with their working schedule
 * and skills. Filters by team, department and skill ("NOC technicians with SonicWall").
 */
export default function WorkforcePage() {
  const { data: me } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  const canManage = !!me?.permissions.includes('workforce.manage');
  const [skillIds, setSkillIds] = useState<string[]>([]);
  const [matchAll, setMatchAll] = useState(false);
  const [team, setTeam] = useState('');
  const [department, setDepartment] = useState('');
  const [includeInactive, setIncludeInactive] = useState(false);
  const [q, setQ] = useState('');

  const { data: skills } = useQuery({ queryKey: ['skills', false], queryFn: () => api.skills(false) });
  const { data: people, isLoading, error } = useQuery({
    queryKey: ['workforce-people', { skillIds, matchAll, includeInactive }],
    queryFn: () => api.workforcePeople({ skills: skillIds, matchAll, includeInactive }),
  });

  const teams = useMemo(() => [...new Set((people ?? []).flatMap((p) => p.teams))].sort(), [people]);
  const departments = useMemo(() => [...new Set((people ?? []).flatMap((p) => p.departments))].sort(), [people]);
  const rows = (people ?? []).filter((p) =>
    (!team || p.teams.includes(team)) && (!department || p.departments.includes(department))
    && (!q.trim() || `${p.displayName} ${p.email}`.toLowerCase().includes(q.trim().toLowerCase())));
  const withoutSchedule = rows.filter((p) => p.isActive && !p.scheduleSummary).length;

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="flex items-center gap-2 text-2xl font-semibold tracking-tight"><CalendarClock size={22} className="text-brand" aria-hidden="true" /> Workforce</h1>
          <p className="max-w-prose text-sm text-[var(--muted)]">
            When each person normally works and what they are skilled in, so work can be planned against real capacity.
            Working hours here are a planning boundary, not attendance.
          </p>
        </div>
        {canManage && (
          <Link href="/dashboard/workforce/skills" className="inline-flex items-center gap-2 rounded-lg border border-[var(--border)] px-3.5 py-2 text-sm font-medium hover:bg-[var(--bg)]">
            <Tags size={16} /> Skills catalogue
          </Link>
        )}
      </div>

      <section aria-label="Filters" className="flex flex-wrap items-center gap-2 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-3">
        <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search name or email…" aria-label="Search people"
          className="min-w-48 flex-1 rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-1.5 text-sm outline-none focus:border-brand" />
        <select value={team} onChange={(e) => setTeam(e.target.value)} aria-label="Team" className="rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-1.5 text-sm">
          <option value="">All teams</option>
          {teams.map((t) => <option key={t} value={t}>{t}</option>)}
        </select>
        <select value={department} onChange={(e) => setDepartment(e.target.value)} aria-label="Department" className="rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-1.5 text-sm">
          <option value="">All departments</option>
          {departments.map((d) => <option key={d} value={d}>{d}</option>)}
        </select>
        <label className="inline-flex items-center gap-1.5 text-xs text-[var(--muted)]">
          <input type="checkbox" checked={includeInactive} onChange={(e) => setIncludeInactive(e.target.checked)} /> Include inactive
        </label>
        {(skills ?? []).length > 0 && (
          <div className="flex w-full flex-wrap items-center gap-1.5 pt-1" role="group" aria-label="Skills">
            <span className="text-xs text-[var(--muted)]">Skills:</span>
            {(skills ?? []).map((s) => {
              const on = skillIds.includes(s.id);
              return (
                <button key={s.id} type="button" aria-pressed={on}
                  onClick={() => setSkillIds((ids) => (on ? ids.filter((i) => i !== s.id) : [...ids, s.id]))}
                  className={`rounded-full border px-2.5 py-0.5 text-xs font-medium ${on ? 'border-brand bg-brand text-brand-fg' : 'border-[var(--border)] hover:bg-[var(--bg)]'}`}>
                  {s.name}
                </button>
              );
            })}
            {skillIds.length > 1 && (
              <select value={matchAll ? 'all' : 'any'} onChange={(e) => setMatchAll(e.target.value === 'all')} aria-label="Skill match"
                className="rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-0.5 text-xs">
                <option value="any">has any of them</option>
                <option value="all">has all of them</option>
              </select>
            )}
          </div>
        )}
      </section>

      {canManage && withoutSchedule > 0 && (
        <p className="text-xs text-[var(--muted)]">{withoutSchedule} {withoutSchedule === 1 ? 'person has' : 'people have'} no working schedule yet. Open someone to set one, then use &ldquo;Apply to others&rdquo; to give it to the rest.</p>
      )}

      {error && <p role="alert" className="text-sm text-red-600 dark:text-red-400">{(error as Error).message}</p>}
      <div className="overflow-x-auto rounded-xl border border-[var(--border)] bg-[var(--surface)]">
        <table className="w-full min-w-[720px] text-sm">
          <thead className="border-b border-[var(--border)] text-left text-[11px] uppercase tracking-wide text-[var(--muted)]">
            <tr>
              <th className="px-4 py-2.5 font-medium">Person</th>
              <th className="px-3 py-2.5 font-medium">Team</th>
              <th className="px-3 py-2.5 font-medium">Working schedule</th>
              <th className="px-3 py-2.5 text-right font-medium">Week</th>
              <th className="px-3 py-2.5 font-medium">Skills</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-[var(--border)]">
            {isLoading && <tr><td colSpan={5} className="px-4 py-6 text-center text-[var(--muted)]">Loading…</td></tr>}
            {!isLoading && rows.length === 0 && <tr><td colSpan={5} className="px-4 py-6 text-center text-[var(--muted)]">Nobody matches these filters.</td></tr>}
            {rows.map((p) => (
              <tr key={p.appUserId} className="align-top">
                <td className="px-4 py-2.5">
                  <Link href={`/dashboard/workforce/people/${p.appUserId}`} className="font-medium hover:underline">{p.displayName}</Link>
                  <div className="flex flex-wrap gap-1 pt-0.5">
                    {!p.isActive && <span className="rounded-full bg-[var(--bg)] px-1.5 text-[10px] font-medium text-[var(--muted)]">Inactive</span>}
                    {p.isActive && !p.isSchedulable && <span className="rounded-full bg-amber-100 px-1.5 text-[10px] font-medium text-amber-900 dark:bg-amber-950 dark:text-amber-200">Not offered for work</span>}
                  </div>
                </td>
                <td className="px-3 py-2.5 text-[var(--muted)]">{p.teams.join(', ') || '—'}</td>
                <td className="px-3 py-2.5">
                  {p.scheduleSummary ? <><span>{p.scheduleSummary}</span><span className="block text-[11px] text-[var(--muted)]">{p.timeZone}</span></>
                    : <span className="text-[var(--muted)]">Not set</span>}
                </td>
                <td className="whitespace-nowrap px-3 py-2.5 text-right tabular-nums">{p.weeklyUsableMinutes != null ? hours(p.weeklyUsableMinutes) : '—'}</td>
                <td className="px-3 py-2.5">
                  <div className="flex flex-wrap gap-1">
                    {p.skills.length === 0 && <span className="text-[var(--muted)]">—</span>}
                    {p.skills.map((s) => (
                      <span key={s.skillId} title={['', 'Basic', 'Proficient', 'Expert'][s.level]}
                        className="whitespace-nowrap rounded-full border border-[var(--border)] px-2 py-0.5 text-[11px]">{s.name}{s.level === 3 ? ' ★' : ''}</span>
                    ))}
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
