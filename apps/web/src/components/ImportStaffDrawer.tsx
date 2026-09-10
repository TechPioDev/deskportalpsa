'use client';

import { useMemo, useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { Upload, Check, AlertTriangle, SkipForward, FileSpreadsheet } from 'lucide-react';
import { api, type ImportResult } from '@/lib/api';

/**
 * Create staff from a spreadsheet.
 *
 * Parsed in the browser rather than uploaded, so nothing about the file leaves this page until an
 * administrator has read the preview and pressed the button. The preview is not decoration: forty
 * rows is well past the number anyone checks by eye, and an import that half-lands cannot be told
 * from a complete one by looking at the result afterwards.
 */

type Row = { displayName: string; email: string; department: string | null };

/**
 * Enough CSV for a staff list: quoted fields, escaped quotes inside them, and CRLF.
 *
 * Not a general parser and not trying to be. It does handle a quoted field containing a comma,
 * because "Sharma, Komal" in a Name column is the first thing that breaks a split(',') and the
 * failure is silent - the row imports with a mangled name rather than reporting anything.
 */
function parseCsv(text: string): string[][] {
  const rows: string[][] = [];
  let row: string[] = [];
  let field = '';
  let quoted = false;

  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quoted) {
      if (c === '"') {
        if (text[i + 1] === '"') { field += '"'; i++; } else quoted = false;
      } else field += c;
      continue;
    }
    if (c === '"') { quoted = true; continue; }
    if (c === ',') { row.push(field); field = ''; continue; }
    if (c === '\r') continue;
    if (c === '\n') { row.push(field); rows.push(row); row = []; field = ''; continue; }
    field += c;
  }
  if (field.length > 0 || row.length > 0) { row.push(field); rows.push(row); }
  return rows.filter((r) => r.some((c) => c.trim().length > 0));
}

/** Finds a column by any of several plausible headings, so the file does not have to be reshaped. */
function columnIndex(headers: string[], candidates: string[]): number {
  const norm = headers.map((h) => h.trim().toLowerCase());
  for (const c of candidates) {
    const i = norm.indexOf(c);
    if (i >= 0) return i;
  }
  return -1;
}

export function ImportStaffDrawer({ onClose, onImported }: { onClose: () => void; onImported: () => void }) {
  const [rows, setRows] = useState<Row[] | null>(null);
  const [fileName, setFileName] = useState('');
  const [parseError, setParseError] = useState<string | null>(null);
  const [roleId, setRoleId] = useState('');
  const [result, setResult] = useState<ImportResult | null>(null);
  const [applied, setApplied] = useState(false);

  const { data: roles } = useQuery({ queryKey: ['staff-roles'], queryFn: api.staffRoles, staleTime: 5 * 60_000 });

  // Technician is the overwhelmingly common answer, so it is preselected — but it stays a choice,
  // because importing forty people into the wrong role is a tedious thing to undo.
  const chosenRole = roleId || roles?.find((r) => r.name === 'Technician')?.id || roles?.[0]?.id || '';

  const preview = useMutation({
    mutationFn: (dryRun: boolean) =>
      api.importStaffUsers({ rows: rows ?? [], roleIds: [chosenRole], dryRun }),
    onSuccess: (r) => { setResult(r); if (!r.dryRun) { setApplied(true); onImported(); } },
  });

  async function onFile(file: File) {
    setParseError(null); setResult(null); setApplied(false);
    setFileName(file.name);
    try {
      const grid = parseCsv(await file.text());
      if (grid.length < 2) throw new Error('That file has a header row and nothing else.');

      const headers = grid[0];
      const iName = columnIndex(headers, ['name', 'full name', 'display name']);
      const iEmail = columnIndex(headers, ['email', 'email address', 'e-mail']);
      const iDept = columnIndex(headers, ['department', 'dept', 'team']);

      if (iName < 0 || iEmail < 0) {
        throw new Error(
          `Could not find a Name and an Email column. Found: ${headers.filter(Boolean).join(', ') || 'nothing'}.`);
      }

      setRows(grid.slice(1).map((r) => ({
        displayName: (r[iName] ?? '').trim(),
        email: (r[iEmail] ?? '').trim(),
        department: iDept >= 0 ? ((r[iDept] ?? '').trim() || null) : null,
      })));
    } catch (e) {
      setRows(null);
      setParseError(e instanceof Error ? e.message : 'That file could not be read.');
    }
  }

  const counts = useMemo(() => ({
    create: result?.rows.filter((r) => Number(r.outcome) === 0).length ?? 0,
    exists: result?.rows.filter((r) => Number(r.outcome) === 1).length ?? 0,
    invalid: result?.rows.filter((r) => Number(r.outcome) === 2).length ?? 0,
  }), [result]);

  return (
    <div className="space-y-4">
      <p className="text-sm text-[var(--muted)]">
        A CSV with <strong>Name</strong> and <strong>Email</strong> columns, and optionally{' '}
        <strong>Department</strong>. Everyone gets the role you choose; the department is matched to
        one that already exists.
      </p>

      <label className="flex cursor-pointer items-center gap-3 rounded-xl border border-dashed border-[var(--border)] bg-[var(--bg)] px-4 py-5 text-sm hover:border-brand">
        <FileSpreadsheet size={20} className="shrink-0 text-[var(--muted)]" aria-hidden="true" />
        <span className="min-w-0 flex-1">
          <span className="block font-medium">{fileName || 'Choose a CSV file'}</span>
          <span className="block text-xs text-[var(--muted)]">
            {rows ? `${rows.length} row${rows.length === 1 ? '' : 's'} read` : 'Nothing is created until you confirm.'}
          </span>
        </span>
        <input type="file" accept=".csv,text/csv" className="sr-only"
          onChange={(e) => { const f = e.target.files?.[0]; if (f) onFile(f); }} />
      </label>

      {parseError && (
        <p role="alert" className="rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm text-red-600 dark:text-red-400">
          {parseError}
        </p>
      )}

      {rows && !applied && (
        <label className="block text-sm">
          <span className="mb-1 block font-medium">Role for everyone in this file</span>
          <select value={chosenRole} onChange={(e) => { setRoleId(e.target.value); setResult(null); }}
            className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand">
            {(roles ?? []).map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
          </select>
        </label>
      )}

      {preview.isError && (
        <p role="alert" className="text-sm text-red-600 dark:text-red-400">
          {preview.error instanceof Error ? preview.error.message : 'The import could not be checked.'}
        </p>
      )}

      {result && (
        <div className="space-y-3">
          <div className="flex flex-wrap gap-2 text-xs">
            <Badge tone="green" icon={Check} label={`${counts.create} to create`} />
            {counts.exists > 0 && <Badge tone="amber" icon={SkipForward} label={`${counts.exists} already here`} />}
            {counts.invalid > 0 && <Badge tone="red" icon={AlertTriangle} label={`${counts.invalid} cannot import`} />}
          </div>

          {/* Every row, not just the failures. Someone about to create forty accounts should be able
              to see the whole list before it happens, including the ones that will be skipped. */}
          <div className="max-h-64 overflow-y-auto rounded-lg border border-[var(--border)]">
            <table className="w-full text-left text-xs">
              <tbody>
                {result.rows.map((r, i) => {
                  const o = Number(r.outcome);
                  return (
                    <tr key={`${r.email}-${i}`} className="border-b border-[var(--border)] last:border-0">
                      <td className="px-3 py-1.5">{r.displayName || <span className="text-[var(--faint)]">(no name)</span>}</td>
                      <td className="px-3 py-1.5 text-[var(--muted)]">{r.email}</td>
                      <td className="px-3 py-1.5 text-right">
                        {o === 0 && <span className="text-green-700 dark:text-green-400">{applied ? 'created' : 'will be created'}</span>}
                        {o === 1 && <span className="text-amber-700 dark:text-amber-300" title={r.reason ?? undefined}>skipped</span>}
                        {o === 2 && <span className="text-red-600 dark:text-red-400" title={r.reason ?? undefined}>{r.reason}</span>}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </div>
      )}

      <div className="flex flex-wrap items-center gap-2 pt-1">
        {!applied && (
          <>
            <button
              onClick={() => preview.mutate(true)}
              disabled={!rows || !chosenRole || preview.isPending}
              className="inline-flex items-center gap-2 rounded-lg border border-[var(--border)] px-3 py-2 text-sm font-medium hover:bg-[var(--bg)] disabled:opacity-50">
              {preview.isPending && result === null ? 'Checking…' : 'Check this file'}
            </button>
            <button
              onClick={() => preview.mutate(false)}
              disabled={!result || counts.create === 0 || preview.isPending}
              className="inline-flex items-center gap-2 rounded-lg bg-brand px-3 py-2 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
              <Upload size={15} aria-hidden="true" />
              {preview.isPending ? 'Importing…' : `Create ${counts.create} user${counts.create === 1 ? '' : 's'}`}
            </button>
          </>
        )}
        <button onClick={onClose}
          className="rounded-lg border border-[var(--border)] px-3 py-2 text-sm hover:bg-[var(--bg)]">
          {applied ? 'Done' : 'Cancel'}
        </button>
      </div>

      {applied && (
        <p className="rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-xs leading-relaxed text-[var(--muted)]">
          Each new account is an invitation until its owner first signs in — the portal binds a person
          to their row by verified email at that point. They will also need board access before any
          ticket is visible to them.
        </p>
      )}
    </div>
  );
}

function Badge({ tone, icon: Icon, label }: {
  tone: 'green' | 'amber' | 'red'; icon: typeof Check; label: string;
}) {
  const tones = {
    green: 'bg-green-50 text-green-700 dark:bg-green-950/50 dark:text-green-300',
    amber: 'bg-amber-50 text-amber-700 dark:bg-amber-950/50 dark:text-amber-300',
    red: 'bg-red-50 text-red-700 dark:bg-red-950/50 dark:text-red-300',
  }[tone];
  return (
    <span className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 font-medium ${tones}`}>
      <Icon size={13} aria-hidden="true" /> {label}
    </span>
  );
}
