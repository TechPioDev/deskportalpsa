'use client';

import { useEffect, useRef, useState } from 'react';
import { Check, Link2 } from 'lucide-react';

/**
 * A link shown once, with a button that copies it and says that it did.
 *
 * The button used to call the clipboard and say nothing. A person pressed it, saw nothing change,
 * and reported "copy not working" (12 October 2026): whether it had copied or not, there was no
 * way to tell. It also gave up silently wherever the browser does not offer the clipboard to the
 * page. So: the button now says "Copied" when it has; where the clipboard is not offered or
 * refuses, the older way is tried (select the field, copy the selection); and where that fails
 * too, the link is left selected and the person is told to press Ctrl+C.
 */
export function CopyLinkField({ value, label, inputClassName, compact = false }: {
  value: string;
  /** What the field is, for a screen reader. */
  label: string;
  inputClassName?: string;
  compact?: boolean;
}) {
  const field = useRef<HTMLInputElement>(null);
  const [state, setState] = useState<'idle' | 'copied' | 'manual'>('idle');
  // A new link is a new thing to copy: the last one's "Copied" does not apply to it.
  useEffect(() => { setState('idle'); }, [value]);
  useEffect(() => {
    if (state !== 'copied') return;
    const back = setTimeout(() => setState('idle'), 4000);
    return () => clearTimeout(back);
  }, [state]);

  const copy = async () => {
    let done = false;
    try {
      if (navigator.clipboard && window.isSecureContext) {
        await navigator.clipboard.writeText(value);
        done = true;
      }
    } catch {
      done = false;
    }
    if (!done && field.current) {
      field.current.focus();
      field.current.select();
      try { done = document.execCommand('copy'); } catch { done = false; }
    }
    if (done) { setState('copied'); return; }
    // Left selected, so that Ctrl+C is all that is needed.
    field.current?.focus();
    field.current?.select();
    setState('manual');
  };

  return (
    <>
      <input ref={field} readOnly value={value} aria-label={label} onFocus={(e) => e.currentTarget.select()}
        className={inputClassName ?? 'min-w-[280px] flex-1 rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-1 font-mono text-xs'} />
      <button type="button" onClick={() => { void copy(); }}
        className={`inline-flex shrink-0 items-center gap-1 rounded-lg border px-2.5 py-1 text-xs font-medium ${state === 'copied'
          ? 'border-emerald-300 bg-emerald-50 text-emerald-700 dark:border-emerald-900 dark:bg-emerald-950/40 dark:text-emerald-300'
          : 'border-[var(--border)] hover:bg-[var(--bg)]'}`}>
        {state === 'copied' ? <Check size={12} aria-hidden="true" /> : <Link2 size={12} aria-hidden="true" />}
        {state === 'copied' ? 'Copied' : 'Copy'}
      </button>
      <span aria-live="polite" className={state === 'idle' ? 'sr-only' : `text-xs ${compact ? '' : 'basis-full'} ${state === 'copied' ? 'text-emerald-700 dark:text-emerald-300' : 'text-amber-700 dark:text-amber-300'}`}>
        {state === 'copied' ? 'The link is copied. Paste it where you want to send it.'
          : state === 'manual' ? 'Your browser did not allow the copy. The link is selected: press Ctrl+C.' : ''}
      </span>
    </>
  );
}
