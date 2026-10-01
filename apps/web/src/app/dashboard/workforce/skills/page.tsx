'use client';

import { useState } from 'react';
import Link from 'next/link';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ArrowLeft, Pencil, Plus } from 'lucide-react';
import { api, type Skill } from '@/lib/api';

const field = 'rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-1.5 text-sm outline-none focus:border-brand';

/**
 * The organization's own skill catalogue. Nothing is built in. A skill is retired, not deleted, so
 * the people who hold it keep it and it can come back.
 */
export default function SkillsPage() {
  const qc = useQueryClient();
  const { data: skills, isLoading, error } = useQuery({ queryKey: ['skills', true], queryFn: () => api.skills(true) });
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const create = useMutation({
    mutationFn: () => api.createSkill(name.trim(), description.trim() || null),
    onSuccess: () => { setName(''); setDescription(''); qc.invalidateQueries({ queryKey: ['skills'] }); },
  });

  return (
    <div className="mx-auto max-w-3xl space-y-5">
      <Link href="/dashboard/workforce" className="inline-flex items-center gap-1.5 text-sm text-[var(--muted)] hover:text-[var(--fg)]">
        <ArrowLeft size={16} /> Workforce
      </Link>
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Skills catalogue</h1>
        <p className="text-sm text-[var(--muted)]">The skills your team plans work by. Retire a skill rather than delete it; whoever holds it keeps it.</p>
      </div>

      <form aria-label="Add a skill" onSubmit={(e) => { e.preventDefault(); if (name.trim()) create.mutate(); }}
        className="flex flex-wrap items-end gap-2 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-3">
        <label className="block min-w-48 flex-1 space-y-1 text-xs font-medium text-[var(--muted)]">
          Skill
          <input value={name} onChange={(e) => setName(e.target.value)} maxLength={80} placeholder="Microsoft 365" aria-label="Skill name" className={`w-full ${field}`} />
        </label>
        <label className="block min-w-48 flex-[2] space-y-1 text-xs font-medium text-[var(--muted)]">
          Description (optional)
          <input value={description} onChange={(e) => setDescription(e.target.value)} maxLength={300} placeholder="Exchange, Teams, Intune" aria-label="Skill description" className={`w-full ${field}`} />
        </label>
        <button type="submit" disabled={!name.trim() || create.isPending}
          className="inline-flex items-center gap-1 rounded-lg bg-brand px-3 py-2 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50"><Plus size={14} /> Add skill</button>
        {create.isError && <p role="alert" className="w-full text-xs text-red-600 dark:text-red-400">{(create.error as Error).message}</p>}
      </form>

      {error && <p role="alert" className="text-sm text-red-600 dark:text-red-400">{(error as Error).message}</p>}
      {isLoading ? <p className="text-sm text-[var(--muted)]">Loading…</p> : (skills ?? []).length === 0 ? (
        <p className="rounded-xl border border-dashed border-[var(--border)] p-6 text-center text-sm text-[var(--muted)]">No skills yet. Add the first one above.</p>
      ) : (
        <ul className="divide-y divide-[var(--border)] rounded-xl border border-[var(--border)] bg-[var(--surface)]">
          {(skills ?? []).map((s) => <SkillRow key={s.id} skill={s} />)}
        </ul>
      )}
    </div>
  );
}

function SkillRow({ skill }: { skill: Skill }) {
  const qc = useQueryClient();
  const [editing, setEditing] = useState(false);
  const [name, setName] = useState(skill.name);
  const [description, setDescription] = useState(skill.description ?? '');
  const update = useMutation({
    mutationFn: (v: { name: string; description: string | null; isActive: boolean }) => api.updateSkill(skill.id, v),
    onSuccess: () => { setEditing(false); qc.invalidateQueries({ queryKey: ['skills'] }); qc.invalidateQueries({ queryKey: ['workforce-people'] }); },
  });
  return (
    <li className="space-y-2 px-4 py-3">
      {editing ? (
        <form aria-label={`Edit ${skill.name}`} className="flex flex-wrap items-center gap-2"
          onSubmit={(e) => { e.preventDefault(); update.mutate({ name: name.trim(), description: description.trim() || null, isActive: skill.isActive }); }}>
          <input value={name} onChange={(e) => setName(e.target.value)} maxLength={80} aria-label="Skill name" className={`min-w-40 flex-1 ${field}`} />
          <input value={description} onChange={(e) => setDescription(e.target.value)} maxLength={300} aria-label="Skill description" className={`min-w-40 flex-[2] ${field}`} />
          <button type="submit" disabled={!name.trim() || update.isPending} className="rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg disabled:opacity-50">Save</button>
          <button type="button" onClick={() => { setEditing(false); setName(skill.name); setDescription(skill.description ?? ''); update.reset(); }}
            className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)]">Cancel</button>
        </form>
      ) : (
        <div className="flex flex-wrap items-center gap-3">
          <div className="min-w-0 flex-1">
            <p className={`text-sm font-medium ${skill.isActive ? '' : 'text-[var(--muted)] line-through'}`}>{skill.name}</p>
            {skill.description && <p className="text-xs text-[var(--muted)]">{skill.description}</p>}
          </div>
          <span className="text-xs tabular-nums text-[var(--muted)]">{skill.holderCount} {skill.holderCount === 1 ? 'person' : 'people'}</span>
          <button type="button" onClick={() => setEditing(true)} aria-label={`Rename ${skill.name}`} className="rounded p-1.5 text-[var(--muted)] hover:bg-[var(--bg)]"><Pencil size={14} /></button>
          <button type="button" disabled={update.isPending}
            onClick={() => update.mutate({ name: skill.name, description: skill.description, isActive: !skill.isActive })}
            className="rounded-lg border border-[var(--border)] px-2.5 py-1 text-xs font-medium hover:bg-[var(--bg)]">{skill.isActive ? 'Retire' : 'Reactivate'}</button>
        </div>
      )}
      {update.isError && <p role="alert" className="text-xs text-red-600 dark:text-red-400">{(update.error as Error).message}</p>}
    </li>
  );
}
