'use client';

import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { X, ImageOff, Download } from 'lucide-react';
import { api } from '@/lib/api';

/**
 * Inline preview for image attachments, in the thread and in the files list.
 *
 * A chip naming a file is the right control for a spreadsheet nobody can read in place. For a
 * screenshot it is the wrong one: the screenshot IS the message, and putting it one click and one
 * tab away means the reply gets written without it having been looked at.
 *
 * WHY THE URL IS FETCHED PER IMAGE AND NOT HELD
 *
 * Download URLs are HMAC-signed and live five minutes. That is short on purpose, and it rules out
 * fetching them with the ticket: a thread left open on a second monitor would render nothing but
 * broken images. Each preview asks for its own URL and re-asks before the signature ages out, so
 * the page can sit open all afternoon and still show the picture.
 */

/**
 * Whether this is something a browser will draw.
 *
 * Driven by the stored content type rather than the file extension: the extension is chosen by
 * whoever uploaded the file, and "invoice.png" holding a PDF would render as a broken image with
 * no way to tell why.
 *
 * SVG is excluded deliberately. It is an image to a browser but a document to an attacker - one
 * can carry script. An <img> tag does not execute it, so this is not the only thing standing
 * between us and that, but a format whose safety depends entirely on the tag it lands in does not
 * belong in an auto-rendering preview.
 */
export function isPreviewableImage(contentType: string): boolean {
  const type = contentType.toLowerCase();
  return ['image/png', 'image/jpeg', 'image/gif', 'image/webp', 'image/bmp', 'image/avif']
    .includes(type);
}

export function AttachmentPreview({ ticketId, attachmentId, fileName, contentType, clean, onDownload }: {
  ticketId: string;
  attachmentId: string;
  fileName: string;
  contentType: string;
  /** Quarantined files are never rendered - the scanner's verdict decides, not the file type. */
  clean: boolean;
  /**
   * Saving the file, offered on the image itself.
   *
   * Passed where the preview REPLACES the file chip rather than sitting above it. The chip was the
   * only way to download, so dropping it without this would quietly remove the ability to keep a
   * copy - the picture would be visible and unobtainable.
   */
  onDownload?: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [broken, setBroken] = useState(false);

  const show = clean && isPreviewableImage(contentType);

  const { data, refetch, isError } = useQuery({
    queryKey: ['attachment-url', ticketId, attachmentId],
    queryFn: () => api.attachmentDownloadUrl(ticketId, attachmentId),
    enabled: show,
    // Comfortably inside the five-minute signature so a URL is replaced before it dies rather
    // than after, and the viewer never meets an expired one.
    staleTime: 3 * 60 * 1000,
    refetchInterval: 3 * 60 * 1000,
    refetchOnWindowFocus: true,
    retry: 1,
  });

  // Escape closes the overlay, and the page behind it must not scroll while it is up - reading a
  // screenshot with the thread sliding underneath is worse than not opening it.
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    const previous = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('keydown', onKey);
      document.body.style.overflow = previous;
    };
  }, [open]);

  if (!show) return null;

  if (isError || broken) {
    return (
      <button
        onClick={() => { setBroken(false); refetch(); }}
        className="mb-1.5 flex items-center gap-2 rounded-lg border border-dashed border-[var(--border)] px-3 py-2 text-xs text-[var(--muted)] hover:border-brand hover:text-brand"
      >
        <ImageOff size={14} aria-hidden="true" />
        Preview did not load — try again
      </button>
    );
  }

  if (!data?.url) {
    return <div className="mb-1.5 h-24 w-40 animate-pulse rounded-lg border border-[var(--border)] bg-[var(--bg)]" />;
  }

  return (
    <>
      <div className="group relative mb-1.5 inline-block max-w-full">
        <button
          onClick={() => setOpen(true)}
          aria-label={`View ${fileName} full size`}
          className="block overflow-hidden rounded-lg border border-[var(--border)] bg-[var(--bg)] hover:border-brand"
        >
          {/* Plain <img>, not next/image: the source is a short-lived signed URL on our own origin,
              so there is nothing for the optimizer to cache and a cached copy would outlive its
              signature. Height-capped so a tall screenshot cannot push the rest of the thread off
              the screen. */}
          <img
            src={data.url}
            alt={fileName}
            onError={() => setBroken(true)}
            className="max-h-64 max-w-full object-contain"
          />
        </button>
        {onDownload && (
          // Hidden until the image is hovered or something inside is focused, so the picture is
          // not permanently wearing a button. On a touch screen there is no hover to wait for, so
          // it stays visible there - the media query is the whole point of the arbitrary variant.
          <button
            onClick={onDownload}
            aria-label={`Download ${fileName}`}
            title={fileName}
            className="absolute right-2 top-2 rounded-md bg-black/55 p-1.5 text-white opacity-100 transition-opacity hover:bg-black/75 focus-visible:opacity-100 [@media(hover:hover)]:opacity-0 [@media(hover:hover)]:group-hover:opacity-100 [@media(hover:hover)]:group-focus-within:opacity-100"
          >
            <Download size={14} aria-hidden="true" />
          </button>
        )}
      </div>

      {open && (
        <div
          role="dialog"
          aria-modal="true"
          aria-label={fileName}
          onClick={() => setOpen(false)}
          className="fixed inset-0 z-50 flex items-center justify-center bg-black/80 p-4"
        >
          <button
            onClick={() => setOpen(false)}
            aria-label="Close preview"
            className="absolute right-4 top-4 rounded-lg bg-white/10 p-2 text-white hover:bg-white/20"
          >
            <X size={20} aria-hidden="true" />
          </button>
          {/* Stop the click on the image itself from closing, or a drag to read the edge of a wide
              screenshot dismisses the thing being read. */}
          <img
            src={data.url}
            alt={fileName}
            onClick={(e) => e.stopPropagation()}
            className="max-h-full max-w-full object-contain"
          />
        </div>
      )}
    </>
  );
}
