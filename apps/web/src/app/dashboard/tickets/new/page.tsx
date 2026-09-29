'use client';

import { useState } from 'react';
import { useRouter } from 'next/navigation';
import { useMutation, useQuery } from '@tanstack/react-query';
import { z } from 'zod';
import { ArrowLeft } from 'lucide-react';
import Link from 'next/link';
import { api } from '@/lib/api';

const FormSchema = z.object({
  title: z.string().min(3, 'Give your request a short title (3+ characters).'),
  description: z.string().optional(),
  priority: z.enum(['LOW', 'NORMAL', 'HIGH', 'CRITICAL']),
});

export default function NewTicketPage() {
  const router = useRouter();
  const [form, setForm] = useState({ title: '', description: '', priority: 'NORMAL', deviceId: '' });
  const [error, setError] = useState<string | null>(null);
  // The company's devices the PSA knows. Only shown when there are some: an empty "Which device?"
  // is a question nobody can answer.
  const { data: devices } = useQuery({ queryKey: ['client-device-choices'], queryFn: api.clientDeviceChoices, retry: false });

  const create = useMutation({
    mutationFn: (body: { title: string; description?: string; priority?: string; deviceId?: string }) => api.createTicket(body),
    onSuccess: (r) => router.push(`/dashboard/tickets/${r.id}`),
    // The server's own words: "That device is not one of your company's devices" is something the
    // person can act on; a generic "could not submit" is not.
    onError: (e) => setError((e as Error).message || 'Could not submit the ticket. Please try again.'),
  });

  function submit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    const parsed = FormSchema.safeParse(form);
    if (!parsed.success) {
      setError(parsed.error.issues[0].message);
      return;
    }
    create.mutate({ ...parsed.data, deviceId: form.deviceId || undefined });
  }

  return (
    <div className="mx-auto max-w-xl space-y-5">
      <Link href="/dashboard/tickets" className="inline-flex items-center gap-1.5 text-sm text-[var(--muted)] hover:text-[var(--fg)]">
        <ArrowLeft size={15} /> Back to tickets
      </Link>
      <div>
        <h1 className="text-xl font-semibold">New ticket</h1>
        <p className="text-sm text-[var(--muted)]">Describe what you need help with — it goes straight to your IT team.</p>
      </div>

      <form onSubmit={submit} className="space-y-4 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-6">
        <Field label="Title">
          <input
            value={form.title}
            onChange={(e) => setForm({ ...form, title: e.target.value })}
            placeholder="e.g. Can't connect to the VPN"
            className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand"
          />
        </Field>
        <Field label="Priority">
          <select
            value={form.priority}
            onChange={(e) => setForm({ ...form, priority: e.target.value })}
            className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand"
          >
            <option value="LOW">Low</option>
            <option value="NORMAL">Normal</option>
            <option value="HIGH">High</option>
            <option value="CRITICAL">Critical</option>
          </select>
        </Field>
        {devices && devices.length > 0 && (
          <Field label="Which device? (optional)">
            <select
              value={form.deviceId}
              onChange={(e) => setForm({ ...form, deviceId: e.target.value })}
              className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand"
            >
              <option value="">Not about a particular device</option>
              {devices.map((d) => <option key={d.id} value={d.id}>{d.name}{d.type ? ` — ${d.type}` : ''}</option>)}
            </select>
          </Field>
        )}
        <Field label="Description">
          <textarea
            value={form.description}
            onChange={(e) => setForm({ ...form, description: e.target.value })}
            rows={5}
            placeholder="What's happening? Include any error messages."
            className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand"
          />
        </Field>

        {error && <p className="text-sm text-red-600 dark:text-red-400">{error}</p>}

        <div className="flex justify-end">
          <button
            type="submit"
            disabled={create.isPending}
            className="rounded-lg bg-brand px-4 py-2 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50"
          >
            {create.isPending ? 'Submitting…' : 'Submit ticket'}
          </button>
        </div>
      </form>
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1.5 block text-sm font-medium">{label}</span>
      {children}
    </label>
  );
}
