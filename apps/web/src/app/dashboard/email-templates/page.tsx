'use client';

import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { MailOpen, Eye, Send, RotateCcw, Save, AlertTriangle, CheckCircle2 } from 'lucide-react';
import { api } from '@/lib/api';
import type { EmailTemplate } from '@/lib/types';

const message = (e: unknown, fallback: string) => (e instanceof Error ? e.message : fallback);
const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }) : '—');

/**
 * The organization's wording for the e-mails PioManage sends: invitations, reminders, the welcome,
 * password resets. Each can be changed, seen as it would arrive, sent to yourself, and put back to
 * the product's default. A template may use only the placeholders its kind offers; the page lists
 * them, and the server refuses anything else.
 */
export default function EmailTemplatesPage() {
  const qc = useQueryClient();
  const list = useQuery({ queryKey: ['email-templates'], queryFn: api.emailTemplates });
  const log = useQuery({ queryKey: ['email-log'], queryFn: api.emailLog, refetchInterval: 30_000 });
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const selected = list.data?.find((t) => t.key === selectedKey) ?? list.data?.[0] ?? null;

  return (
    <div className="space-y-5">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-semibold tracking-tight"><MailOpen size={22} className="text-brand" /> E-mail wording</h1>
        <p className="mt-1 max-w-3xl text-sm text-[var(--muted)]">
          What PioManage writes when it invites someone, reminds them, welcomes them, or sends a password reset. Change the words, see the result,
          send it to yourself, or put the default back. Each e-mail offers a fixed set of placeholders; anything else is refused when you save.
        </p>
      </div>

      {list.isError && <p role="alert" className="text-sm text-rose-600 dark:text-rose-400">{message(list.error, 'The templates could not be read.')}</p>}

      <div className="grid gap-4 lg:grid-cols-[260px_1fr]">
        <nav aria-label="Templates" className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-2">
          {(list.data ?? []).map((t) => (
            <button key={t.key} type="button" onClick={() => setSelectedKey(t.key)}
              className={'block w-full rounded-lg px-3 py-2 text-left text-sm hover:bg-[var(--bg)] ' + (selected?.key === t.key ? 'bg-[var(--bg)] font-medium' : '')}>
              {t.name}
              <span className="block text-xs text-[var(--muted)]">{t.isDefault ? 'Default wording' : `Changed by ${t.updatedByName ?? 'an administrator'}`}</span>
            </button>
          ))}
        </nav>
        {selected && <Editor key={selected.key} template={selected} onSaved={() => qc.invalidateQueries({ queryKey: ['email-templates'] })} />}
      </div>

      <section aria-label="Recent e-mails" className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
        <h2 className="border-b border-[var(--border)] px-4 py-2.5 text-sm font-semibold">Recent e-mails</h2>
        {log.isSuccess && log.data.length === 0 && <p className="px-4 py-6 text-sm text-[var(--muted)]">Nothing has been sent yet.</p>}
        {log.isSuccess && log.data.length > 0 && (
          <div className="overflow-x-auto">
            <table className="w-full min-w-[640px] text-left text-sm">
              <thead className="text-xs text-[var(--muted)]"><tr><th className="px-4 py-2 font-medium">When</th><th className="px-2 py-2 font-medium">To</th><th className="px-2 py-2 font-medium">Subject</th><th className="px-4 py-2 font-medium">Result</th></tr></thead>
              <tbody className="divide-y divide-[var(--border)]">
                {log.data.map((e) => (
                  <tr key={e.id}>
                    <td className="px-4 py-2 text-xs text-[var(--muted)]">{when(e.sentAt)}</td>
                    <td className="px-2 py-2">{e.to}</td>
                    <td className="px-2 py-2">{e.subject}</td>
                    <td className="px-4 py-2 text-xs">{e.succeeded ? <span className="text-green-700 dark:text-green-400">Sent</span> : <span className="text-rose-600 dark:text-rose-400">Not sent: {e.error}</span>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  );
}

function Editor({ template, onSaved }: { template: EmailTemplate; onSaved: () => void }) {
  const [subject, setSubject] = useState(template.subject);
  const [body, setBody] = useState(template.body);
  const [button, setButton] = useState(template.buttonLabel ?? '');
  const [preview, setPreview] = useState<{ subject: string; html: string } | null>(null);
  useEffect(() => { setSubject(template.subject); setBody(template.body); setButton(template.buttonLabel ?? ''); setPreview(null); }, [template]);
  const draft = { subject, body, buttonLabel: template.hasLink ? (button || null) : null };
  const dirty = subject !== template.subject || body !== template.body || (template.hasLink && (button || null) !== (template.buttonLabel ?? null));

  const save = useMutation({ mutationFn: () => api.emailTemplateSave(template.key, draft), onSuccess: onSaved });
  const reset = useMutation({ mutationFn: () => api.emailTemplateReset(template.key), onSuccess: onSaved });
  const show = useMutation({ mutationFn: () => api.emailTemplatePreview(template.key, dirty ? draft : null), onSuccess: (r) => setPreview({ subject: r.subject, html: r.htmlBody }) });
  const test = useMutation({ mutationFn: () => api.emailTemplateTest(template.key) });
  const busy = save.isPending || reset.isPending || show.isPending || test.isPending;
  const field = 'mt-1 w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm text-[var(--fg)] outline-none focus:border-brand';

  return (
    <section aria-label={template.name} className="space-y-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4">
      <div>
        <h2 className="text-base font-semibold">{template.name}</h2>
        <p className="text-xs text-[var(--muted)]">{template.description}</p>
      </div>
      <label className="block text-xs font-medium text-[var(--muted)]">Subject<input value={subject} onChange={(e) => setSubject(e.target.value)} className={field} /></label>
      <label className="block text-xs font-medium text-[var(--muted)]">Body<textarea value={body} onChange={(e) => setBody(e.target.value)} rows={8} className={field + ' font-mono text-[13px]'} /></label>
      {template.hasLink && (
        <label className="block text-xs font-medium text-[var(--muted)]">Button label (the button carries the link)<input value={button} onChange={(e) => setButton(e.target.value)} className={field} /></label>
      )}
      <p className="text-xs text-[var(--muted)]">Placeholders this e-mail offers: {template.placeholders.map((p) => <code key={p} className="mx-0.5 rounded bg-[var(--bg)] px-1">{'{{' + p + '}}'}</code>)}</p>
      <div className="flex flex-wrap items-center gap-2">
        <button type="button" disabled={busy || !dirty} onClick={() => save.mutate()}
          className="inline-flex items-center gap-1.5 rounded-lg bg-brand px-3 py-1.5 text-xs font-medium text-brand-fg hover:opacity-90 disabled:opacity-40"><Save size={13} /> Save</button>
        <button type="button" disabled={busy} onClick={() => show.mutate()}
          className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-3 py-1.5 text-xs font-medium hover:bg-[var(--bg)] disabled:opacity-40"><Eye size={13} /> Preview</button>
        <button type="button" disabled={busy || dirty} title={dirty ? 'Save first; the test sends what is saved.' : undefined} onClick={() => test.mutate()}
          className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-3 py-1.5 text-xs font-medium hover:bg-[var(--bg)] disabled:opacity-40"><Send size={13} /> Send me a test</button>
        {!template.isDefault && (
          <button type="button" disabled={busy} onClick={() => { if (window.confirm('Put the default wording back? Your changes to this e-mail are lost.')) reset.mutate(); }}
            className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-3 py-1.5 text-xs font-medium hover:bg-[var(--bg)] disabled:opacity-40"><RotateCcw size={13} /> Default wording</button>
        )}
        {save.isSuccess && !dirty && <span className="inline-flex items-center gap-1 text-xs text-green-700 dark:text-green-400"><CheckCircle2 size={13} /> Saved</span>}
        {test.isSuccess && <span className="inline-flex items-center gap-1 text-xs text-green-700 dark:text-green-400"><CheckCircle2 size={13} /> Sent to {test.data.to}</span>}
      </div>
      {(save.isError || reset.isError || show.isError || test.isError) && (
        <p role="alert" className="inline-flex items-start gap-1.5 text-xs text-rose-600 dark:text-rose-400"><AlertTriangle size={13} className="mt-0.5 shrink-0" />
          {message(save.error ?? reset.error ?? show.error ?? test.error, 'That could not be done.')}</p>
      )}
      {preview && (
        <div className="rounded-lg border border-[var(--border)]">
          <p className="border-b border-[var(--border)] px-3 py-2 text-xs"><span className="text-[var(--muted)]">Subject:</span> {preview.subject}</p>
          <iframe title="Preview" srcDoc={preview.html} sandbox="" className="h-[420px] w-full bg-white" />
        </div>
      )}
    </section>
  );
}
