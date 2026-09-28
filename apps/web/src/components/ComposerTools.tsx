'use client';

import { useState, type RefObject } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Bold, Italic, List, ListOrdered, Code, Quote, Table, Link2, MessageSquareText } from 'lucide-react';
import { api } from '@/lib/api';
import { fillPlaceholders, type PlaceholderValues } from '@/lib/canned';

/**
 * The formatting row above the reply box, and the canned-response picker beside it.
 *
 * A toolbar used to sit here and was removed because its buttons were wired to nothing. These edit
 * the text in the box — markdown the thread already renders — so what the buttons do is visible in
 * the text itself and survives being sent to a PSA as plain words.
 */
export function ComposerTools({ textarea, value, onChange, ticketId, placeholders, maxLength, showPsaHint }: {
  textarea: RefObject<HTMLTextAreaElement | null>;
  value: string;
  onChange: (next: string) => void;
  ticketId: string;
  placeholders: PlaceholderValues;
  maxLength: number;
  /** A PSA may show the marks as typed rather than as formatting; say so where it applies. */
  showPsaHint: boolean;
}) {
  const [picking, setPicking] = useState(false);
  const { data: canned } = useQuery({
    queryKey: ['canned-for', ticketId], queryFn: () => api.cannedResponsesFor(ticketId), retry: false, staleTime: 60_000,
  });

  /** Replace the selection with what `edit` makes of it, and put the caret where a person expects. */
  const apply = (edit: (selected: string) => { text: string; caret?: number }) => {
    const el = textarea.current;
    const start = el?.selectionStart ?? value.length;
    const end = el?.selectionEnd ?? value.length;
    const { text, caret } = edit(value.slice(start, end));
    const next = (value.slice(0, start) + text + value.slice(end)).slice(0, maxLength);
    onChange(next);
    requestAnimationFrame(() => {
      if (!el) return;
      el.focus();
      const at = start + (caret ?? text.length);
      el.setSelectionRange(at, at);
    });
  };

  const wrap = (mark: string, fallback: string) => apply((s) => {
    const inner = s || fallback;
    return { text: `${mark}${inner}${mark}`, caret: s ? undefined : mark.length + inner.length };
  });
  /** Prefix each selected line — or start a new line — with the marker a list or quote uses. */
  const prefix = (marker: (i: number) => string) => apply((s) => {
    const atLineStart = (textarea.current?.selectionStart ?? 0) === 0
      || value[(textarea.current?.selectionStart ?? 1) - 1] === '\n';
    const lines = (s || '').split('\n');
    const body = lines.map((l, i) => `${marker(i)}${l}`).join('\n');
    return { text: `${atLineStart ? '' : '\n'}${body}` };
  });

  const insertCanned = (id: string) => {
    const r = canned?.find((c) => c.id === id);
    setPicking(false);
    if (!r) return;
    const filled = fillPlaceholders(r.body, placeholders);
    // Into an empty box it is the reply; into a draft it goes where the caret is, so a greeting
    // already typed is not thrown away.
    if (!value.trim()) onChange(filled.slice(0, maxLength));
    else apply(() => ({ text: filled }));
  };

  const btn = 'rounded p-1.5 text-[var(--muted)] hover:bg-[var(--surface)] hover:text-[var(--fg)]';
  return (
    <div className="flex flex-wrap items-center gap-0.5 border-b border-[var(--border)] px-2 py-1">
      <button type="button" className={btn} title="Bold" aria-label="Bold" onClick={() => wrap('**', 'bold text')}><Bold size={14} /></button>
      <button type="button" className={btn} title="Italic" aria-label="Italic" onClick={() => wrap('*', 'italic text')}><Italic size={14} /></button>
      <button type="button" className={btn} title="Bulleted list" aria-label="Bulleted list" onClick={() => prefix(() => '- ')}><List size={14} /></button>
      <button type="button" className={btn} title="Numbered list" aria-label="Numbered list" onClick={() => prefix((i) => `${i + 1}. `)}><ListOrdered size={14} /></button>
      <button type="button" className={btn} title="Quote" aria-label="Quote" onClick={() => prefix(() => '> ')}><Quote size={14} /></button>
      <button type="button" className={btn} title="Code" aria-label="Code"
        onClick={() => apply((s) => s.includes('\n') ? { text: `\n\`\`\`\n${s}\n\`\`\`\n` } : { text: `\`${s || 'command'}\`` })}>
        <Code size={14} />
      </button>
      <button type="button" className={btn} title="Link" aria-label="Link"
        onClick={() => apply((s) => ({ text: `[${s || 'link text'}](https://)`, caret: (s || 'link text').length + 11 }))}>
        <Link2 size={14} />
      </button>
      <button type="button" className={btn} title="Table" aria-label="Table"
        onClick={() => apply(() => ({ text: '\n| Item | Detail |\n|---|---|\n|  |  |\n' }))}>
        <Table size={14} />
      </button>

      <span className="mx-1 h-4 w-px bg-[var(--border)]" />

      {picking ? (
        <select autoFocus defaultValue="" onChange={(e) => e.target.value && insertCanned(e.target.value)}
          onBlur={() => setPicking(false)} aria-label="Insert a canned response"
          className="max-w-[16rem] rounded-lg border border-[var(--border)] bg-[var(--surface)] px-2 py-1 text-xs outline-none focus:border-brand">
          <option value="">{(canned ?? []).length === 0 ? 'No canned responses yet' : 'Insert a response…'}</option>
          {(canned ?? []).map((r) => <option key={r.id} value={r.id}>{r.name}{r.boardName ? ` · ${r.boardName}` : ''}</option>)}
        </select>
      ) : (
        <button type="button" onClick={() => setPicking(true)}
          className="inline-flex items-center gap-1.5 rounded-lg px-2 py-1 text-xs font-medium text-[var(--muted)] hover:bg-[var(--surface)] hover:text-[var(--fg)]">
          <MessageSquareText size={14} /> Canned response
        </button>
      )}

      {showPsaHint && (
        <span className="ml-auto hidden text-[10px] text-[var(--faint)] sm:inline"
          title="The portal shows formatting. Some PSAs show the marks as they were typed.">
          Formatting may show as plain marks in the PSA
        </span>
      )}
    </div>
  );
}
