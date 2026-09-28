'use client';

import { useState, type ReactNode } from 'react';
import { ImageIcon } from 'lucide-react';

/**
 * Renders a ticket note's body the way a person wants to read it. PSA notes (ConnectWise
 * especially) arrive as markdown-ish text full of `**bold**`, numbered lists, image markup and
 * enormous pre-signed URLs — shown raw, a single note can be a 19,000-character wall of link text.
 *
 * Deliberately NOT an HTML renderer: provider content is untrusted, so this never touches
 * dangerouslySetInnerHTML. It tokenizes a small markdown subset into React elements — bold, italic,
 * code, links, images, bare URLs, headings, lists, quotes, fenced code blocks and tables — and
 * leaves everything else as literal text. Unsupported markup
 * degrades to plain text, never to markup injection.
 *
 * Long notes collapse past a threshold with a Show-more toggle, so one pasted knowledge-base
 * article cannot swallow the whole conversation.
 */

const COLLAPSE_AT = 700;

const httpOnly = (url: string) => url.startsWith('http://') || url.startsWith('https://');

/** A pre-signed storage URL can be 500+ characters — display a short stand-in, keep the real href. */
function shortUrl(url: string): string {
  try {
    const u = new URL(url);
    const path = u.pathname.length > 24 ? `…${u.pathname.slice(-20)}` : u.pathname;
    return `${u.hostname}${path}`;
  } catch {
    return url.length > 42 ? `${url.slice(0, 40)}…` : url;
  }
}

/** One-line plain-text teaser for truncated previews: markdown stripped, images named, URLs shortened. */
export function notePreview(body: string): string {
  return body
    .replace(/!\[([^\]]*)\]\(([^)\s]+)\)/g, (_m: string, alt: string) => `[${alt || 'image'}]`)
    .replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, '$1')
    .replace(/\*\*([^*\n]+)\*\*/g, '$1')
    .replace(/`([^`\n]+)`/g, '$1')
    .replace(/https?:\/\/[^\s<>()"']+/g, (u: string) => shortUrl(u))
    .replace(/\s+/g, ' ')
    .trim();
}

function InlineImage({ src, alt }: { src: string; alt: string }) {
  const [failed, setFailed] = useState(false);
  if (failed) {
    // Pre-signed image links expire; a broken-image icon row reads as a bug. Fall back to a chip
    // that says what it is and still opens the source (which may prompt the PSA's own auth).
    return (
      <a href={src} target="_blank" rel="noopener noreferrer"
        className="my-0.5 inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-1 text-xs text-[var(--muted)] hover:text-[var(--fg)]">
        <ImageIcon size={13} /> {alt || 'Image'} (opens in the PSA)
      </a>
    );
  }
  return (
    // eslint-disable-next-line @next/next/no-img-element
    <img src={src} alt={alt} loading="lazy" referrerPolicy="no-referrer"
      onError={() => setFailed(true)}
      className="my-1 block max-h-64 max-w-full rounded-lg border border-[var(--border)] object-contain" />
  );
}

// Italic is *single stars* only, never _underscores_: snake_case_names and file_paths turn up in
// notes constantly, and italicising the middle of one would corrupt it on screen.
const INLINE = /!\[([^\]]*)\]\(([^)\s]+)\)|\[([^\]]+)\]\(([^)\s]+)\)|\*\*([^*\n]+)\*\*|`([^`\n]+)`|(https?:\/\/[^\s<>()"']+)|(?<![*\w])\*([^*\s][^*\n]*?)\*(?![*\w])/g;

function renderInline(text: string): ReactNode[] {
  const out: ReactNode[] = [];
  let last = 0;
  let k = 0;
  for (const m of text.matchAll(INLINE)) {
    if (m.index! > last) out.push(text.slice(last, m.index));
    const [, imgAlt, imgSrc, linkText, linkHref, bold, code, bareUrl, italic] = m;
    if (imgSrc !== undefined) {
      out.push(httpOnly(imgSrc) ? <InlineImage key={k++} src={imgSrc} alt={imgAlt ?? ''} /> : m[0]);
    } else if (linkHref !== undefined) {
      out.push(httpOnly(linkHref)
        ? <a key={k++} href={linkHref} target="_blank" rel="noopener noreferrer" className="text-brand underline underline-offset-2 hover:opacity-80">{linkText}</a>
        : m[0]);
    } else if (bold !== undefined) {
      out.push(<strong key={k++}>{bold}</strong>);
    } else if (code !== undefined) {
      out.push(<code key={k++} className="rounded bg-[var(--bg)] px-1 py-0.5 font-mono text-[0.85em]">{code}</code>);
    } else if (italic !== undefined) {
      out.push(<em key={k++}>{italic}</em>);
    } else if (bareUrl !== undefined) {
      out.push(<a key={k++} href={bareUrl} target="_blank" rel="noopener noreferrer" title={bareUrl}
        className="break-all text-brand underline underline-offset-2 hover:opacity-80">{shortUrl(bareUrl)}</a>);
    }
    last = m.index! + m[0].length;
  }
  if (last < text.length) out.push(text.slice(last));
  return out;
}

function renderLine(line: string, key: number): ReactNode {
  const heading = line.match(/^#{1,6}\s+(.*)$/);
  if (heading) return <div key={key} className="mt-2 font-semibold">{renderInline(heading[1])}</div>;

  const listItem = line.match(/^(\s*)(?:[-*•]|\d{1,3}[.)])\s+(.*)$/);
  if (listItem) {
    const marker = line.trim().match(/^([-*•]|\d{1,3}[.)])/)![1];
    return (
      <div key={key} className="flex gap-2 pl-2">
        <span className="shrink-0 text-[var(--muted)]">{/^[-*•]$/.test(marker) ? '•' : marker}</span>
        <span className="min-w-0">{renderInline(listItem[2])}</span>
      </div>
    );
  }

  const quote = line.match(/^>\s?(.*)$/);
  if (quote) {
    return (
      <div key={key} className="border-l-2 border-[var(--border)] pl-3 text-[var(--muted)]">{renderInline(quote[1])}</div>
    );
  }

  if (line.trim() === '') return <div key={key} className="h-2" />;
  return <div key={key}>{renderInline(line)}</div>;
}

/** Cells of one `| a | b |` row, trimmed, with the outer pipes dropped. */
function cells(line: string): string[] {
  return line.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map((c) => c.trim());
}

const TABLE_RULE = /^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$/;

/**
 * Groups the lines that belong together — a fenced code block, a table — and renders everything else
 * a line at a time as before. A table is only a table when its second line is the `|---|---|` rule;
 * a single line that happens to contain pipes is left as the text it is.
 */
function renderBlocks(body: string): ReactNode[] {
  const lines = body.split('\n');
  const out: ReactNode[] = [];
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];

    if (line.trim().startsWith('```')) {
      const code: string[] = [];
      let j = i + 1;
      while (j < lines.length && !lines[j].trim().startsWith('```')) code.push(lines[j++]);
      out.push(
        <pre key={i} className="my-1 overflow-x-auto rounded-lg bg-[var(--bg)] px-3 py-2 font-mono text-[12px] leading-relaxed">
          {code.join('\n')}
        </pre>);
      i = j; // the closing fence, or the end of the note if it was never closed
      continue;
    }

    if (line.trim().startsWith('|') && i + 1 < lines.length && TABLE_RULE.test(lines[i + 1])) {
      const head = cells(line);
      const rows: string[][] = [];
      let j = i + 2;
      while (j < lines.length && lines[j].trim().startsWith('|')) rows.push(cells(lines[j++]));
      out.push(
        <div key={i} className="my-1 overflow-x-auto">
          <table className="min-w-full border-collapse text-[13px]">
            <thead>
              <tr>{head.map((h, c) => <th key={c} className="border border-[var(--border)] bg-[var(--bg)] px-2 py-1 text-left font-semibold">{renderInline(h)}</th>)}</tr>
            </thead>
            <tbody>
              {rows.map((r, ri) => (
                <tr key={ri}>{head.map((_, c) => <td key={c} className="border border-[var(--border)] px-2 py-1 align-top">{renderInline(r[c] ?? '')}</td>)}</tr>
              ))}
            </tbody>
          </table>
        </div>);
      i = j - 1;
      continue;
    }

    out.push(renderLine(line, i));
  }
  return out;
}

export function NoteBody({ body }: { body: string }) {
  const [expanded, setExpanded] = useState(false);
  const long = body.length > COLLAPSE_AT;

  return (
    <div className="mt-1 text-sm">
      {/* The clipped preview fades out via a mask, not a painted gradient — notes sit on
          differently-colored cards (internal amber, time blue…) and a gradient would have to
          guess the background. */}
      <div className={`min-w-0 break-words ${long && !expanded ? 'max-h-52 overflow-hidden [mask-image:linear-gradient(180deg,#000_calc(100%-3rem),transparent)]' : ''}`}>
        {renderBlocks(body)}
      </div>
      {long && (
        <button type="button" onClick={() => setExpanded((v) => !v)}
          className="mt-1 text-xs font-medium text-brand hover:underline">
          {expanded ? 'Show less' : `Show more (${Math.round(body.length / 1000)}k characters)`}
        </button>
      )}
    </div>
  );
}
