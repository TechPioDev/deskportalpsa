'use client';

import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Check, ChevronLeft, ChevronRight, RefreshCw, ShieldCheck, X } from 'lucide-react';
import { api, type ConnectionSettings } from '@/lib/api';
import type { ConnectionSummary, ProviderCatalogEntry } from '@/lib/types';

// In this order because each step needs the one before: nothing can be tested until it is saved,
// nothing discovered until the PSA has let the portal in, nothing previewed until the scope is set.
const STEPS = ['Choose PSA', 'Details', 'Credentials', 'Test', 'Discover', 'Mapping', 'Sync scope', 'Preview', 'Enable'] as const;

const OUTCOME: Record<string, { label: string; tone: string }> = {
  Pass: { label: 'Pass', tone: 'bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300' },
  Fail: { label: 'Fail', tone: 'bg-rose-100 text-rose-700 dark:bg-rose-950 dark:text-rose-300' },
  Warn: { label: 'Warning', tone: 'bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300' },
  NotTested: { label: 'Not tried', tone: 'bg-[var(--bg)] text-[var(--muted)]' },
  Available: { label: 'Available', tone: 'bg-sky-100 text-sky-700 dark:bg-sky-950 dark:text-sky-300' },
  Unavailable: { label: 'Not available', tone: 'bg-[var(--bg)] text-[var(--muted)]' },
};

function Chip({ outcome }: { outcome: string }) {
  const o = OUTCOME[outcome] ?? { label: outcome, tone: 'bg-[var(--bg)] text-[var(--muted)]' };
  return <span className={'shrink-0 rounded px-1.5 py-0.5 text-[11px] font-medium ' + o.tone}>{o.label}</span>;
}

function Field({ label, value, onChange, type = 'text', placeholder, hint }: {
  label: string; value: string; onChange: (v: string) => void; type?: string; placeholder?: string; hint?: string | null;
}) {
  return (
    <label className="block">
      <span className="mb-1.5 block text-sm font-medium">{label}</span>
      <input
        type={type}
        value={value}
        placeholder={placeholder}
        autoComplete="off"
        onChange={(e) => onChange(e.target.value)}
        className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand"
      />
      {hint && <span className="mt-1 block text-xs text-[var(--muted)]">{hint}</span>}
    </label>
  );
}

/** A number the PSA was asked for. null = it could not say, which is not the same as none. */
function Figure({ label, value }: { label: string; value: number | null }) {
  return (
    <div className="rounded-lg border border-[var(--border)] px-3 py-2.5">
      <p className="text-[11px] text-[var(--muted)]">{label}</p>
      <p className="text-lg font-semibold tabular-nums">{value === null ? <span className="text-sm font-normal text-[var(--muted)]">not known</span> : value.toLocaleString()}</p>
    </div>
  );
}

const message = (e: unknown, fallback: string) => (e instanceof Error ? e.message : fallback);

/**
 * Adds a PSA connection one decision at a time. The connection is saved, switched off, at the end
 * of step 3, and is only switched on by the last step: closing the wizard part-way leaves it in
 * setup, where its card offers to carry on.
 */
export function ConnectionWizard({ providers, resume, onClose, onEnabled }: {
  providers: ProviderCatalogEntry[];
  /** A connection saved earlier and still in setup: the wizard carries on from its test. */
  resume: ConnectionSummary | null;
  onClose: () => void;
  onEnabled: (id: string) => void;
}) {
  const qc = useQueryClient();
  const available = providers.filter((p) => p.available);
  const planned = providers.filter((p) => !p.available);

  const [step, setStep] = useState(resume ? 4 : 1);
  const [provider, setProvider] = useState<number | null>(resume ? Number(resume.provider) : null);
  const [connectionId, setConnectionId] = useState<string | null>(resume?.id ?? null);
  const [form, setForm] = useState<Record<string, string>>({
    name: resume?.name ?? '', apiEndpoint: resume?.apiEndpoint ?? '', tenantIdentifier: resume?.tenantIdentifier ?? '',
  });
  const [acknowledged, setAcknowledged] = useState(false);
  const def = available.find((p) => Number(p.provider) === provider) ?? null;
  const set = (key: string, value: string) => setForm((f) => ({ ...f, [key]: value }));

  // ---- step 3: save (switched off) ----------------------------------------------------------
  const save = useMutation({
    mutationFn: async () => {
      if (!def || provider === null) throw new Error('Choose a PSA first.');
      const typed = Object.fromEntries(def.credentials.map((c) => [c.key, form[c.key] ?? '']).filter(([, v]) => v !== ''));
      if (connectionId) {
        // Already saved: a blank credential keeps what is stored. Still in setup, so "enabled" is
        // not this form's to decide - the server ignores it until the last step switches it on.
        await api.updateConnection(connectionId, {
          name: form.name, apiEndpoint: form.apiEndpoint, tenantIdentifier: form.tenantIdentifier || undefined,
          isEnabled: false, logoUrl: resume?.logoUrl ?? undefined,
          credentials: Object.keys(typed).length > 0 ? typed : undefined,
        });
        return connectionId;
      }
      const created = await api.createConnection({
        name: form.name, provider, apiEndpoint: form.apiEndpoint,
        tenantIdentifier: form.tenantIdentifier || undefined, credentials: typed,
      });
      return created.id;
    },
    onSuccess: (id) => {
      setConnectionId(id);
      qc.invalidateQueries({ queryKey: ['connections'] });
      resetCheck();
      setStep(4);
    },
  });

  // ---- step 4: the test, line by line -------------------------------------------------------
  const { mutate: runCheck, data: report, isPending: checking, error: checkError, reset: resetCheck } = useMutation({
    mutationFn: (id: string) => api.checkConnection(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['connections'] }),
  });
  // Arriving at the test runs it: nobody should have to ask.
  useEffect(() => {
    if (step === 4 && connectionId && !report && !checking && !checkError) runCheck(connectionId);
  }, [step, connectionId, report, checking, checkError, runCheck]);

  // ---- step 5: what the PSA offers ----------------------------------------------------------
  const fields = useQuery({
    queryKey: ['wizard-fields', connectionId],
    queryFn: () => api.connectionFields(connectionId!),
    enabled: step >= 5 && !!connectionId, retry: false,
  });
  const refreshFields = useMutation({
    mutationFn: () => api.refreshConnectionFields(connectionId!),
    onSuccess: (f) => qc.setQueryData(['wizard-fields', connectionId], f),
  });

  // ---- step 6: what its values become -------------------------------------------------------
  const coverage = useQuery({
    queryKey: ['wizard-coverage', connectionId],
    queryFn: () => api.connectionMappingCoverage(connectionId!),
    enabled: step === 6 && !!connectionId, retry: false, staleTime: 0,
  });

  // ---- step 7: what to bring in -------------------------------------------------------------
  const settings = useQuery({
    queryKey: ['wizard-settings', connectionId],
    queryFn: () => api.connectionSettings(connectionId!),
    enabled: step >= 7 && !!connectionId, retry: false,
  });
  const [scope, setScope] = useState<ConnectionSettings | null>(null);
  useEffect(() => {
    if (settings.data && !scope) setScope(settings.data);
  }, [settings.data, scope]);
  const chosenQueues = (scope?.filterQueueIds ?? '').split(',').map((s) => s.trim()).filter(Boolean);
  const toggleQueue = (id: string) => {
    if (!scope) return;
    const next = chosenQueues.includes(id) ? chosenQueues.filter((q) => q !== id) : [...chosenQueues, id];
    setScope({ ...scope, filterQueueIds: next.length > 0 ? next.join(',') : null });
  };
  const saveScope = useMutation({
    mutationFn: () => api.saveConnectionSettings(connectionId!, scope!),
    onSuccess: (saved) => {
      setScope(saved);
      qc.invalidateQueries({ queryKey: ['connection-settings', connectionId] });
      setStep(8);
    },
  });

  // ---- steps 8 and 9: how much, and what stands in the way ------------------------------------
  const preview = useQuery({
    queryKey: ['wizard-preview', connectionId],
    queryFn: () => api.connectionPreview(connectionId!),
    enabled: step === 8 && !!connectionId, retry: false, staleTime: 0,
  });
  const preflight = useQuery({
    queryKey: ['wizard-preflight', connectionId],
    queryFn: () => api.connectionPreflight(connectionId!),
    enabled: step >= 8 && !!connectionId, retry: false, staleTime: 0,
  });
  const warnings = (preflight.data?.items ?? []).filter((i) => i.outcome === 'Warn');
  const blockers = (preflight.data?.items ?? []).filter((i) => i.outcome === 'Fail');

  const enable = useMutation({
    mutationFn: () => api.activateConnection(connectionId!),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['connections'] });
      onEnabled(connectionId!);
    },
  });

  // What "Next" needs, step by step.
  const credentialsGiven = def
    ? connectionId !== null || def.credentials.every((c) => (form[c.key] ?? '').trim() !== '')
    : false;
  const canGoOn: Record<number, boolean> = {
    1: def !== null,
    2: form.name.trim() !== '' && form.apiEndpoint.trim() !== '',
    3: credentialsGiven && !save.isPending,
    4: report?.passed === true,
    5: fields.isSuccess,
    6: true,
    7: scope !== null && !saveScope.isPending,
    8: preflight.isSuccess,
  };
  const next = () => {
    if (step === 3) save.mutate();
    else if (step === 7) saveScope.mutate();
    else setStep(step + 1);
  };
  const nextLabel = step === 3 ? (save.isPending ? 'Saving…' : 'Save and test') : step === 7 ? (saveScope.isPending ? 'Saving…' : 'Save scope') : 'Next';

  return (
    <section aria-label="Add a PSA connection" className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
      <header className="flex items-start justify-between gap-3 border-b border-[var(--border)] px-5 py-4">
        <div>
          <h2 className="text-sm font-semibold">{resume ? `Finish setting up ${resume.name}` : 'Add a PSA connection'}</h2>
          <p className="mt-0.5 text-xs text-[var(--muted)]">
            Step {step} of {STEPS.length}: {STEPS[step - 1]}.
            {connectionId && step < 9 && ' Saved in setup and switched off. You can close this and carry on later from its card.'}
          </p>
        </div>
        <button onClick={onClose} aria-label="Close" className="rounded-lg p-1.5 text-[var(--muted)] hover:bg-[var(--bg)]"><X size={16} /></button>
      </header>

      <ol className="flex flex-wrap gap-x-4 gap-y-1.5 border-b border-[var(--border)] px-5 py-3 text-xs">
        {STEPS.map((label, i) => {
          const n = i + 1;
          const done = n < step;
          return (
            <li key={label} aria-current={n === step ? 'step' : undefined}
              className={'flex items-center gap-1.5 ' + (n === step ? 'font-semibold text-[var(--fg)]' : 'text-[var(--muted)]')}>
              <span className={'flex h-5 w-5 items-center justify-center rounded-full text-[10px] font-semibold tabular-nums ' +
                (done ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300'
                  : n === step ? 'bg-brand text-brand-fg' : 'bg-[var(--bg)] text-[var(--muted)]')}>
                {done ? <Check size={11} aria-hidden="true" /> : n}
              </span>
              {label}
            </li>
          );
        })}
      </ol>

      <div className="space-y-4 px-5 py-5">
        {step === 1 && (
          <>
            <p className="text-sm text-[var(--muted)]">
              {connectionId ? 'The PSA cannot be changed once the connection is saved.' : 'Which PSA is this connection to?'}
            </p>
            <div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
              {available.map((p) => {
                const chosen = Number(p.provider) === provider;
                return (
                  <button key={String(p.provider)} type="button" aria-pressed={chosen} disabled={connectionId !== null && !chosen}
                    onClick={() => setProvider(Number(p.provider))}
                    className={'rounded-lg border px-3 py-3 text-left text-sm font-medium disabled:opacity-50 ' +
                      (chosen ? 'border-brand bg-brand/10 text-brand dark:bg-brand/25 dark:text-brand-soft' : 'border-[var(--border)] hover:bg-[var(--bg)]')}>
                    {p.name}
                  </button>
                );
              })}
            </div>
            {planned.length > 0 && (
              <div>
                <h3 className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-[var(--faint)]">Coming soon</h3>
                {/* Named, and not selectable: there is no connector behind any of these yet. */}
                <ul className="flex flex-wrap gap-2">
                  {planned.map((p) => (
                    <li key={String(p.provider)} className="rounded-lg border border-dashed border-[var(--border)] px-2.5 py-1.5 text-xs text-[var(--muted)]">{p.name}</li>
                  ))}
                </ul>
              </div>
            )}
          </>
        )}

        {step === 2 && def && (
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="Name" value={form.name} onChange={(v) => set('name', v)}
              hint="What your team will call it, for example the client environment it belongs to." />
            <Field label="API endpoint" value={form.apiEndpoint} onChange={(v) => set('apiEndpoint', v)}
              placeholder={def.endpointExample ?? ''} hint={def.endpointHint} />
            <Field label={`${def.tenantIdentifierLabel ?? 'Tenant identifier'} (optional)`} value={form.tenantIdentifier}
              onChange={(v) => set('tenantIdentifier', v)} hint="A label for your own reference. It is not sent to the PSA." />
          </div>
        )}

        {step === 3 && def && (
          <>
            <p className="flex items-center gap-2 rounded-lg bg-[var(--bg)] px-3 py-2 text-xs text-[var(--muted)]">
              <ShieldCheck size={14} className="shrink-0" aria-hidden="true" />
              Credentials go to your secret vault. They are not kept in the database and are never shown again.
              {connectionId && ' Leave a field blank to keep what is stored.'}
            </p>
            <div className="grid gap-3 sm:grid-cols-2">
              {def.credentials.map((c) => (
                <Field key={c.key} label={c.label} type={c.secret ? 'password' : 'text'} hint={c.hint}
                  placeholder={connectionId ? 'unchanged' : ''} value={form[c.key] ?? ''} onChange={(v) => set(c.key, v)} />
              ))}
            </div>
            <p className="text-xs text-[var(--muted)]">
              Saving does not switch the connection on. It is saved in setup, then tested.
            </p>
            {save.isError && <p role="alert" className="text-sm text-rose-600 dark:text-rose-400">{message(save.error, 'The connection could not be saved.')}</p>}
          </>
        )}

        {step === 4 && (
          <>
            {checking && <p className="flex items-center gap-2 text-sm text-[var(--muted)]"><RefreshCw size={14} className="animate-spin" aria-hidden="true" /> Testing the connection…</p>}
            {checkError && <p role="alert" className="text-sm text-rose-600 dark:text-rose-400">{message(checkError, 'The test could not be run.')}</p>}
            {report && (
              <>
                <ul className="divide-y divide-[var(--border)] rounded-lg border border-[var(--border)]">
                  {report.checks.map((c) => (
                    <li key={c.key} className="flex flex-wrap items-start gap-x-3 gap-y-1 px-3 py-2.5">
                      <Chip outcome={c.outcome} />
                      <div className="min-w-0 flex-1">
                        <p className="text-sm font-medium">
                          {c.name}
                          {c.required && <span className="ml-1.5 text-[11px] font-normal text-[var(--faint)]">needed by the sync</span>}
                        </p>
                        {c.detail && <p className="mt-0.5 break-words text-xs text-[var(--muted)]">{c.detail}</p>}
                      </div>
                    </li>
                  ))}
                </ul>
                <p className={'text-sm ' + (report.passed ? 'text-emerald-600 dark:text-emerald-400' : 'text-rose-600 dark:text-rose-400')}>
                  {report.passed
                    ? 'The PSA accepted the connection and everything the sync needs worked. Nothing was changed in the PSA.'
                    : 'Not ready: a line the sync needs did not pass. Go back to correct the details, or fix the API account in the PSA, then run the test again.'}
                </p>
              </>
            )}
            {!checking && connectionId && (
              <button type="button" onClick={() => runCheck(connectionId)}
                className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium hover:bg-[var(--bg)]">
                <RefreshCw size={13} aria-hidden="true" /> Run the test again
              </button>
            )}
          </>
        )}

        {step === 5 && (
          <>
            {fields.isLoading && <p className="text-sm text-[var(--muted)]">Reading what this PSA offers…</p>}
            {fields.isError && <p role="alert" className="text-sm text-rose-600 dark:text-rose-400">{message(fields.error, 'The PSA could not be read.')}</p>}
            {fields.data && (
              <>
                <p className="text-sm text-[var(--muted)]">This is what the PSA lists today. The next steps are chosen from it.</p>
                <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
                  {([
                    ['Queues or boards', fields.data.queuesOrBoards], ['Statuses', fields.data.statuses], ['Priorities', fields.data.priorities],
                    ['Categories', fields.data.categories], ['Work types', fields.data.workTypes], ['Technicians', fields.data.technicians],
                  ] as const).map(([title, items]) => (
                    <details key={title} className="rounded-lg border border-[var(--border)] px-3 py-2.5">
                      <summary className="cursor-pointer text-sm font-medium">
                        {title} <span className="font-normal tabular-nums text-[var(--muted)]">({items.length})</span>
                      </summary>
                      {items.length === 0 ? (
                        <p className="mt-1.5 text-xs text-[var(--muted)]">None found.</p>
                      ) : (
                        <ul className="mt-1.5 space-y-0.5 text-xs text-[var(--muted)]">
                          {items.slice(0, 30).map((o) => <li key={o.value}>{o.label}</li>)}
                          {items.length > 30 && <li>and {items.length - 30} more</li>}
                        </ul>
                      )}
                    </details>
                  ))}
                </div>
                <button type="button" onClick={() => refreshFields.mutate()} disabled={refreshFields.isPending}
                  className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium hover:bg-[var(--bg)] disabled:opacity-50">
                  <RefreshCw size={13} className={refreshFields.isPending ? 'animate-spin' : undefined} aria-hidden="true" /> Read again from the PSA
                </button>
              </>
            )}
          </>
        )}

        {step === 6 && (
          <>
            {coverage.isLoading && <p className="text-sm text-[var(--muted)]">Checking the mapping…</p>}
            {coverage.isError && <p role="alert" className="text-sm text-rose-600 dark:text-rose-400">{message(coverage.error, 'The mapping could not be checked.')}</p>}
            {coverage.data && (
              <>
                <p className="text-sm text-[var(--muted)]">
                  {coverage.data.unmapped === 0
                    ? 'Every status and priority this PSA lists has a mapping.'
                    : `${coverage.data.unmapped} ${coverage.data.unmapped === 1 ? 'value has' : 'values have'} no mapping. Nothing is guessed: an unmapped value arrives exactly as the PSA sends it, until you map it.`}
                </p>
                <div className="grid gap-4 lg:grid-cols-2">
                  {([['Statuses', coverage.data.statuses], ['Priorities', coverage.data.priorities]] as const).map(([title, rows]) => (
                    <div key={title} className="overflow-x-auto rounded-lg border border-[var(--border)]">
                      <table className="w-full text-left text-sm">
                        <caption className="border-b border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-left text-xs font-semibold">{title}</caption>
                        <thead className="text-xs text-[var(--muted)]">
                          <tr><th className="px-3 py-1.5 font-medium">In the PSA</th><th className="px-3 py-1.5 font-medium">In the portal</th></tr>
                        </thead>
                        <tbody className="divide-y divide-[var(--border)]">
                          {rows.length === 0 && <tr><td colSpan={2} className="px-3 py-2 text-xs text-[var(--muted)]">The PSA lists none.</td></tr>}
                          {rows.map((r) => (
                            <tr key={r.value}>
                              <td className="px-3 py-1.5">{r.label}</td>
                              <td className="px-3 py-1.5">
                                {r.mapsTo === null
                                  ? <span className="rounded bg-amber-100 px-1.5 py-0.5 text-[11px] font-medium text-amber-700 dark:bg-amber-950 dark:text-amber-300">Not mapped</span>
                                  : <>{r.mapsTo}{r.byFallbackRule && <span className="ml-1.5 text-xs text-[var(--faint)]">by a catch-all rule</span>}</>}
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  ))}
                </div>
                <p className="flex flex-wrap items-center gap-3 text-xs text-[var(--muted)]">
                  <a href="/dashboard/mappings" target="_blank" rel="noreferrer" className="font-medium text-brand underline underline-offset-2">Open Field Mapping in a new tab</a>
                  <button type="button" onClick={() => coverage.refetch()} className="inline-flex items-center gap-1 rounded border border-[var(--border)] px-2 py-1 font-medium hover:bg-[var(--bg)]">
                    <RefreshCw size={12} className={coverage.isFetching ? 'animate-spin' : undefined} aria-hidden="true" /> Check again
                  </button>
                  You can also map them after the connection is on.
                </p>
              </>
            )}
          </>
        )}

        {step === 7 && (
          <>
            {!scope && !settings.isError && <p className="text-sm text-[var(--muted)]">Loading the scope…</p>}
            {settings.isError && <p role="alert" className="text-sm text-rose-600 dark:text-rose-400">{message(settings.error, 'The scope could not be loaded.')}</p>}
            {scope && (
              <div className="space-y-4">
                <fieldset>
                  <legend className="mb-1.5 text-sm font-medium">Which tickets</legend>
                  <div className="flex flex-wrap gap-x-5 gap-y-1.5 text-sm">
                    <label className="flex items-center gap-2"><input type="checkbox" checked={scope.importOpenTickets} onChange={(e) => setScope({ ...scope, importOpenTickets: e.target.checked })} /> Open tickets</label>
                    <label className="flex items-center gap-2"><input type="checkbox" checked={scope.importClosedTickets} onChange={(e) => setScope({ ...scope, importClosedTickets: e.target.checked })} /> Closed tickets</label>
                  </div>
                  {!scope.importOpenTickets && !scope.importClosedTickets && (
                    <p className="mt-1 text-xs text-rose-600 dark:text-rose-400">With neither ticked, nothing would be imported.</p>
                  )}
                </fieldset>
                <label className="block max-w-xs">
                  <span className="mb-1.5 block text-sm font-medium">Only tickets active in the last (days)</span>
                  <input type="number" min={1} value={scope.filterActiveWithinDays ?? ''} placeholder="no limit"
                    onChange={(e) => setScope({ ...scope, filterActiveWithinDays: e.target.value === '' ? null : Math.max(1, Number(e.target.value)) })}
                    className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand" />
                  <span className="mt-1 block text-xs text-[var(--muted)]">Leave empty to bring in tickets of any age.</span>
                </label>
                <fieldset>
                  <legend className="mb-1.5 text-sm font-medium">Queues or boards</legend>
                  <p className="mb-1.5 text-xs text-[var(--muted)]">
                    {chosenQueues.length === 0 ? 'None ticked: tickets come from every queue or board.' : `Tickets come from the ${chosenQueues.length} ticked.`}
                  </p>
                  {(fields.data?.queuesOrBoards.length ?? 0) === 0 ? (
                    <p className="text-xs text-[var(--muted)]">This PSA listed no queues or boards to choose from.</p>
                  ) : (
                    <div className="grid gap-1.5 text-sm sm:grid-cols-2 lg:grid-cols-3">
                      {fields.data!.queuesOrBoards.map((q) => (
                        <label key={q.value} className="flex items-center gap-2">
                          <input type="checkbox" checked={chosenQueues.includes(q.value)} onChange={() => toggleQueue(q.value)} /> {q.label}
                        </label>
                      ))}
                    </div>
                  )}
                </fieldset>
                <fieldset>
                  <legend className="mb-1.5 text-sm font-medium">With each ticket</legend>
                  <div className="flex flex-wrap gap-x-5 gap-y-1.5 text-sm">
                    <label className="flex items-center gap-2"><input type="checkbox" checked={scope.importNotes} onChange={(e) => setScope({ ...scope, importNotes: e.target.checked })} /> Its notes</label>
                    <label className="flex items-center gap-2"><input type="checkbox" checked={scope.syncAttachments} onChange={(e) => setScope({ ...scope, syncAttachments: e.target.checked })} /> Its attachments</label>
                  </div>
                </fieldset>
                <p className="text-xs text-[var(--muted)]">Limits by client or technician, and the defaults for new tickets, are under Sync settings on the connection once it is saved.</p>
                {saveScope.isError && <p role="alert" className="text-sm text-rose-600 dark:text-rose-400">{message(saveScope.error, 'The scope could not be saved.')}</p>}
              </div>
            )}
          </>
        )}

        {step === 8 && (
          <>
            {preview.isLoading && <p className="flex items-center gap-2 text-sm text-[var(--muted)]"><RefreshCw size={14} className="animate-spin" aria-hidden="true" /> Asking the PSA how much there is…</p>}
            {preview.isError && <p role="alert" className="text-sm text-rose-600 dark:text-rose-400">{message(preview.error, 'The preview could not be made.')}</p>}
            {preview.data && (
              <>
                <p className="text-sm text-[var(--muted)]">Asked of the PSA just now, under the scope you chose. Nothing has been imported.</p>
                <div className="grid gap-2 sm:grid-cols-3 lg:grid-cols-6">
                  <Figure label="Clients" value={preview.data.clients} />
                  <Figure label="Technicians" value={preview.data.technicians} />
                  <Figure label="Open tickets" value={preview.data.openTickets} />
                  <Figure label="All tickets" value={preview.data.allTickets} />
                  <Figure label="Would be imported" value={preview.data.ticketsToImport} />
                  <Figure label="Unmapped values" value={preview.data.unmapped} />
                </div>
                {preview.data.notes.length > 0 && (
                  <ul className="list-disc space-y-0.5 pl-5 text-xs text-[var(--muted)]">
                    {preview.data.notes.map((n) => <li key={n}>{n}</li>)}
                  </ul>
                )}
              </>
            )}
            <PreflightList data={preflight.data} loading={preflight.isLoading} />
          </>
        )}

        {step === 9 && (
          <>
            <dl className="grid gap-x-6 gap-y-1 text-sm sm:grid-cols-[auto_1fr]">
              <dt className="text-[var(--muted)]">Connection</dt><dd className="font-medium">{form.name}</dd>
              <dt className="text-[var(--muted)]">PSA</dt><dd>{def?.name}</dd>
              <dt className="text-[var(--muted)]">Address</dt><dd className="break-all">{form.apiEndpoint}</dd>
            </dl>
            <PreflightList data={preflight.data} loading={preflight.isLoading} />
            {blockers.length > 0 && (
              <p className="text-sm text-rose-600 dark:text-rose-400">It cannot be switched on until the failed {blockers.length === 1 ? 'line is' : 'lines are'} put right.</p>
            )}
            {blockers.length === 0 && warnings.length > 0 && (
              <label className="flex items-start gap-2 text-sm">
                <input type="checkbox" className="mt-0.5" checked={acknowledged} onChange={(e) => setAcknowledged(e.target.checked)} />
                I have read the {warnings.length === 1 ? 'warning' : `${warnings.length} warnings`} above and want to switch it on as it is.
              </label>
            )}
            <p className="text-xs text-[var(--muted)]">
              Switching it on starts the sync on the next cycle. It can be paused, disabled or archived from its card at any time.
            </p>
            {enable.isError && <p role="alert" className="text-sm text-rose-600 dark:text-rose-400">{message(enable.error, 'It could not be switched on.')}</p>}
          </>
        )}
      </div>

      <footer className="flex items-center gap-2 border-t border-[var(--border)] bg-[var(--bg)] px-5 py-3">
        <button type="button" onClick={() => setStep(step - 1)} disabled={step === 1}
          className="inline-flex items-center gap-1 rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm disabled:opacity-40">
          <ChevronLeft size={14} aria-hidden="true" /> Back
        </button>
        <span className="flex-1" />
        {step < 9 ? (
          <button type="button" onClick={next} disabled={!canGoOn[step]}
            className="inline-flex items-center gap-1 rounded-lg bg-brand px-4 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
            {nextLabel} <ChevronRight size={14} aria-hidden="true" />
          </button>
        ) : (
          <button type="button" onClick={() => enable.mutate()}
            disabled={enable.isPending || !preflight.data?.canEnable || (warnings.length > 0 && !acknowledged)}
            className="rounded-lg bg-brand px-4 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
            {enable.isPending ? 'Switching on…' : 'Enable connection'}
          </button>
        )}
      </footer>
    </section>
  );
}

function PreflightList({ data, loading }: { data?: { items: { key: string; name: string; outcome: string; detail: string }[] }; loading: boolean }) {
  if (loading) return <p className="text-sm text-[var(--muted)]">Checking what stands in the way…</p>;
  if (!data) return null;
  return (
    <div>
      <h3 className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-[var(--faint)]">Before it is switched on</h3>
      <ul className="divide-y divide-[var(--border)] rounded-lg border border-[var(--border)]">
        {data.items.map((i) => (
          <li key={i.key} className="flex flex-wrap items-start gap-x-3 gap-y-1 px-3 py-2.5">
            <Chip outcome={i.outcome} />
            <div className="min-w-0 flex-1">
              <p className="text-sm font-medium">{i.name}</p>
              <p className="mt-0.5 break-words text-xs text-[var(--muted)]">{i.detail}</p>
            </div>
          </li>
        ))}
      </ul>
    </div>
  );
}
