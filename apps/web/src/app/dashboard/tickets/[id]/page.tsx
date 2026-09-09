'use client';

import { use, useEffect, useRef, useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  ArrowLeft, ChevronLeft, ChevronRight, ChevronDown, Pencil, MoreHorizontal, Paperclip,
  Send, ArrowUpDown, Lock, Monitor, Wifi, Mail, KeyRound, Cpu, Ticket,
  Copy, RefreshCw, Download, Clock, Trash2, Check, X, ClipboardList, UserCog, ExternalLink, AlertTriangle} from 'lucide-react';
import { useTimer } from '@/components/TimerProvider';
import { NoteBody, notePreview } from '@/components/NoteBody';
import { AssistantRail } from '@/components/AssistantRail';
import { AttachmentPreview, isPreviewableImage } from '@/components/AttachmentPreview';
import { api, type AssigneeOptions } from '@/lib/api';
import type { TicketDetail } from '@/lib/types';

/// Whether the time-entry list is open. Shared across tickets on purpose: a technician who wants
/// the list expanded wants it expanded on every ticket, not once per ticket id.
const TIME_PANEL_KEY = 'desk.ticket.timeEntries.open';

/// How many of the most recent messages a long thread opens on.
const RECENT_NOTE_COUNT = 4;

const STATUS_TONE: Record<string, string> = {
  NEW: 'bg-blue-100 text-blue-700 dark:bg-blue-950 dark:text-blue-300',
  IN_PROGRESS: 'bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300',
  WAITING_CUSTOMER: 'bg-violet-100 text-violet-700 dark:bg-violet-950 dark:text-violet-300',
  ON_HOLD: 'bg-orange-100 text-orange-700 dark:bg-orange-950 dark:text-orange-300',
  RESOLVED: 'bg-green-100 text-green-700 dark:bg-green-950 dark:text-green-300',
  CLOSED: 'bg-slate-200 text-slate-600 dark:bg-slate-800 dark:text-slate-300',
};
const STATUSES = ['NEW', 'IN_PROGRESS', 'WAITING_CUSTOMER', 'ON_HOLD', 'RESOLVED', 'CLOSED'];
const PRIORITY_TONE: Record<string, string> = {
  LOW: 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300',
  NORMAL: 'bg-blue-100 text-blue-700 dark:bg-blue-950 dark:text-blue-300',
  HIGH: 'bg-orange-100 text-orange-700 dark:bg-orange-950 dark:text-orange-300',
  CRITICAL: 'bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300',
};
function categoryIcon(cat?: string | null, title?: string) {
  const s = `${cat ?? ''} ${title ?? ''}`.toLowerCase();
  if (/password|access|login|account|vpn/.test(s)) return Lock;
  if (/network|wifi|wi-fi|firewall/.test(s)) return Wifi;
  if (/email|outlook|mail|365/.test(s)) return Mail;
  if (/hardware|printer|laptop|monitor|disk/.test(s)) return Monitor;
  if (/software|application|install/.test(s)) return Cpu;
  if (/key|reset/.test(s)) return KeyRound;
  return Ticket;
}
function fmt(iso: string, seconds = false): string {
  const d = new Date(iso);
  const date = d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
  const time = d.toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit', ...(seconds ? { second: '2-digit' } : {}) });
  return `${date} · ${time}`;
}
const initials = (name: string) => name.split(' ').map((n) => n[0]).join('').slice(0, 2).toUpperCase();

// PSAs record the day time was worked, not the moment. Rendering a clock time turns a midnight
// placeholder into a claim the technician worked at 5:30am.
const fmtDay = (iso: string) =>
  new Date(iso).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });

// Sub-kilobyte files round to "0 KB", which reads as an empty upload.
function fmtSize(bytes: number) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

// Technicians think in minutes; a bare "0.17h" is unreadable at a glance. Show both, as the PSA's
// own decimal is what gets invoiced.
function fmtDuration(hours: number) {
  const mins = Math.round(hours * 60);
  if (mins < 60) return `${mins}m`;
  const h = Math.floor(mins / 60);
  const m = mins % 60;
  return m ? `${h}h ${m}m` : `${h}h`;
}

type TicketAttachment = TicketDetail['attachments'][number];

/**
 * Picks who works the ticket and which queue it sits on. Roles are shown next to each name because
 * "who can take this" is a role question first — an Engineer and a Help Desk tech covering the same
 * board are not interchangeable, and the provider only exposes that through queue coverage.
 */
function AssignPanel({ options, currentTechnicianId, currentQueueId, currentAppUserId, pending, error, onCancel, onSave }: {
  options: AssigneeOptions | undefined;
  currentTechnicianId: string | null;
  currentQueueId: string | null;
  currentAppUserId: string | null;
  pending: boolean;
  error: string | null;
  onCancel: () => void;
  onSave: (body: { technicianExternalId?: string; queueOrBoardId?: string; roleId?: string; appUserId?: string }) => void;
}) {
  const [technician, setTechnician] = useState(currentTechnicianId ?? '');
  const [queue, setQueue] = useState('');
  const [role, setRole] = useState('');
  const [portalUser, setPortalUser] = useState(currentAppUserId ?? '');

  if (!options) return <p className="text-xs text-[var(--muted)]">Loading technicians…</p>;

  // Only worth asking when the person genuinely holds more than one role here; otherwise the
  // server picks the single role they have on this queue and the field is noise.
  const roleOptions = options.technicians.find((t) => t.id === technician)?.roleOptions ?? [];

  const changed = (technician && technician !== currentTechnicianId)
    || (queue && queue !== currentQueueId)
    || (portalUser && portalUser !== currentAppUserId);
  return (
    <div className="space-y-3">
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        <label className="block">
          <span className="mb-1 block text-xs font-medium">Technician</span>
          <select value={technician} onChange={(e) => { setTechnician(e.target.value); setRole(''); }}
            className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand">
            <option value="">— unchanged —</option>
            {options.technicians.map((t) => (
              <option key={t.id} value={t.id}>{t.roles.length ? `${t.name} · ${t.roles.join(', ')}` : t.name}</option>
            ))}
          </select>
          <span className="mt-1 block text-xs text-[var(--muted)]">
            {options.filteredByQueue
              ? 'Technicians who cover this queue, with the role they hold on it.'
              : options.filteredByRole
                ? 'Technicians who hold a role in the PSA. This queue has no specific coverage, so all of them are listed.'
                : 'This PSA does not publish role or queue coverage, so everyone is listed.'}
          </span>
        </label>
        {roleOptions.length > 1 && (
          <label className="block">
            <span className="mb-1 block text-xs font-medium">Role</span>
            <select value={role} onChange={(e) => setRole(e.target.value)}
              className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand">
              <option value="">— their role on this queue —</option>
              {roleOptions.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
            </select>
            <span className="mt-1 block text-xs text-[var(--muted)]">They hold several — pick which one they take this in.</span>
          </label>
        )}
        <label className="block">
          <span className="mb-1 block text-xs font-medium">Queue / board</span>
          <select value={queue} onChange={(e) => setQueue(e.target.value)}
            className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand">
            <option value="">— unchanged —</option>
            {options.queuesOrBoards.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
          </select>
          <span className="mt-1 block text-xs text-[var(--muted)]">Moving a ticket can change who covers it.</span>
        </label>
        <label className="block">
          <span className="mb-1 block text-xs font-medium">Working it (portal)</span>
          <select value={portalUser} onChange={(e) => setPortalUser(e.target.value)}
            className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand">
            <option value="">— unchanged —</option>
            {options.portalTechnicians.map((u) => (
              <option key={u.id} value={u.id}>{u.name}</option>
            ))}
          </select>
          <span className="mt-1 block text-xs text-[var(--muted)]">
            Who on your team is actually working this. Stays in the portal — the PSA is not told, and
            their name does not appear there.
          </span>
        </label>
      </div>
      {error && <p className="text-xs text-red-600 dark:text-red-400">{error}</p>}
      <div className="flex items-center gap-2">
        <button
          onClick={() => onSave({
            technicianExternalId: technician || undefined,
            queueOrBoardId: queue || undefined,
            roleId: role || undefined,
            appUserId: portalUser || undefined,
          })}
          disabled={pending || !changed}
          className="inline-flex items-center gap-1.5 rounded-lg bg-brand px-3 py-2 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
          <Check size={15} /> {pending ? 'Saving…' : 'Save assignment'}
        </button>
        <button onClick={onCancel} className="rounded-lg border border-[var(--border)] px-3 py-2 text-sm hover:bg-[var(--bg)]">Cancel</button>
      </div>
    </div>
  );
}

/** One file, rendered the same way under a reply and in the loose-files list. */
function AttachmentChip({ a, provider, ticketId, onDownload }: {
  a: TicketAttachment; provider: number; ticketId: string; onDownload: (id: string) => void;
}) {
  const clean = String(a.scanStatus) === '1' || String(a.scanStatus) === 'Clean';

  // An image that renders in the thread needs no chip under it. The chip exists to stand in for a
  // file you cannot see; once the picture is there, repeating its name and size is furniture. The
  // download it used to carry moves onto the image itself.
  if (clean && isPreviewableImage(a.contentType)) {
    return (
      <AttachmentPreview
        ticketId={ticketId} attachmentId={a.id} fileName={a.fileName}
        contentType={a.contentType} clean={clean} onDownload={() => onDownload(a.id)}
      />
    );
  }

  return (
    <span className="inline-flex max-w-full items-center gap-1.5 rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-1 text-xs">
      <Paperclip size={12} className="shrink-0 text-[var(--muted)]" />
      <span className="truncate">{a.fileName}</span>
      {a.fromProvider && (
        <span title={a.authorName ? `Attached by ${a.authorName}` : undefined} className="shrink-0 text-[var(--faint)]">
          · {providerLabel(provider)}
        </span>
      )}
      <span className="shrink-0 text-[var(--faint)]">{fmtSize(a.sizeBytes)}</span>
      {clean
        ? <button onClick={() => onDownload(a.id)} aria-label={`Download ${a.fileName}`}
            className="shrink-0 rounded p-0.5 text-[var(--muted)] hover:text-brand"><Download size={13} /></button>
        : <span className="shrink-0 rounded bg-red-100 px-1 py-0.5 font-medium text-red-700 dark:bg-red-950 dark:text-red-300">Quarantined</span>}
    </span>
  );
}

// PSAs distinguish "do not bill" from "no charge"; the boolean alone flattens that away.
function billableLabel(option: string, billable: boolean) {
  if (option === 'NoCharge') return 'No charge';
  if (option === 'DoNotBill') return 'Do not bill';
  return billable ? 'Billable' : 'No charge';
}

// ProviderType: 1 = ConnectWise, 2 = Autotask.
const providerLabel = (provider: number) => (provider === 1 ? 'ConnectWise' : provider === 2 ? 'Autotask' : 'the PSA');
const providerAbbrev = (provider: number) => (provider === 1 ? 'CW' : provider === 2 ? 'AT' : 'PSA');

export default function TicketDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params);
  const qc = useQueryClient();
  const router = useRouter();
  const [comment, setComment] = useState('');
  const [uploading, setUploading] = useState(false);
  // Files chosen in the composer are held until the reply is sent, so they can be filed against
  // that message rather than dropped loose on the ticket.
  const [pendingFiles, setPendingFiles] = useState<File[]>([]);
  const [assignOpen, setAssignOpen] = useState(false);
  const [dragOver, setDragOver] = useState(false);
  // Newest at the top. A ticket is opened to find out where it stands now, and the current state
  // was the last thing on screen. It also puts the older history below the "show earlier" button
  // rather than above it, so the thread reads outward from the present in one direction.
  const [oldestFirst, setOldestFirst] = useState(false);
  const [menuOpen, setMenuOpen] = useState(false);
  const [workType, setWorkType] = useState('');
  const [workRole, setWorkRole] = useState('');
  const [editEntry, setEditEntry] = useState<{ id: string; hours: string; notes: string } | null>(null);
  // Which entries' notes are expanded — a long CW note clipped to one line was unreadable with no way to open it.
  const [expandedNotes, setExpandedNotes] = useState<Set<string>>(new Set());
  // Time entries start collapsed: the header already carries the count and both totals, so the
  // list is detail rather than headline, and six rows of it pushed the conversation off-screen.
  // The stored preference is read AFTER mount — reading localStorage during render makes server
  // and client disagree about the first paint, which React resolves by discarding the markup.
  const [timeOpen, setTimeOpen] = useState(false);
  // Reset per ticket rather than remembered: opening a ticket is when you want the current state,
  // and a preference to see all sixteen on one ticket says nothing about the next one.
  const [showAllNotes, setShowAllNotes] = useState(false);
  useEffect(() => {
    try {
      const saved = window.localStorage.getItem(TIME_PANEL_KEY);
      if (saved !== null) setTimeOpen(saved === '1');
    } catch {
      // Private windows and blocked site data throw on access; the default stands.
    }
  }, []);
  const toggleTime = () => setTimeOpen((prev) => {
    const next = !prev;
    try { window.localStorage.setItem(TIME_PANEL_KEY, next ? '1' : '0'); } catch { /* the preference is a nicety */ }
    return next;
  });
  const timer = useTimer();
  const fileRef = useRef<HTMLInputElement>(null);

  const { data: ticket, isLoading, isError } = useQuery({ queryKey: ['ticket', id], queryFn: () => api.getTicket(id) });
  const { data: list } = useQuery({ queryKey: ['tickets'], queryFn: api.listTickets });
  // Only fetched once the picker is opened: it costs a provider round trip for coverage data that
  // most visits to a ticket never need.
  const { data: assignOpts } = useQuery({
    queryKey: ['assignees', id],
    queryFn: () => api.ticketAssignees(id),
    enabled: assignOpen,
    retry: false,
  });
  const assign = useMutation({
    mutationFn: (body: { technicianExternalId?: string; queueOrBoardId?: string; roleId?: string }) => api.assignTicket(id, body),
    onSuccess: () => {
      setAssignOpen(false);
      [['ticket', id], ['tickets'], ['team']].forEach((k) => qc.invalidateQueries({ queryKey: k }));
    },
  });

  const { data: timeOpts } = useQuery({ queryKey: ['time-options', id], queryFn: () => api.ticketTimeOptions(id), enabled: !!ticket, retry: false });

  function startTimerHere() {
    if (!ticket) return;
    if (timer.running && timer.target?.ticketId !== id &&
        !window.confirm('A timer is already running for another ticket. Attach it to this one?')) return;
    timer.attach({ ticketId: id, ref: ticket.externalTicketId, title: ticket.title });
    timer.start();
  }

  // Time logged alongside a reply, in one send. Kept OUTSIDE the mutation's failure path: once the
  // note has posted, failing the whole mutation over a time entry would tell the user to resend —
  // and resending would duplicate the note. A failed side-step is reported specifically instead.
  const [replyHours, setReplyHours] = useState('');
  const [replyBillable, setReplyBillable] = useState('Billable');
  // Separate from the reply body: the timesheet and the client are different audiences.
  const [replyTimeNotes, setReplyTimeNotes] = useState('');
  const [replySideError, setReplySideError] = useState<string | null>(null);
  // Staff-only composer powers (mirrors the old help desk: note + time + status in ONE post).
  // Gated on view-all — the same signal the API's staff branch keys on.
  const { data: me } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  const isStaff = me?.permissions.includes('tickets.view.all') ?? false;
  // Mirror the API's own gates: a pure CLIENT login carries neither of these, and showing the
  // control anyway just manufactures a 403. Same keys the endpoints demand, not guesses.
  const canLogTime = me?.permissions.includes('tickets.time.log') ?? false;
  // Provider 2 = Autotask, which rejects a time entry whose summary notes are blank.
  const notesRequired = Number(ticket?.provider) === 2;
  const canUpdate = me?.permissions.includes('tickets.update') ?? false;
  // Same key the Assistant settings page and its nav entry demand — only someone holding it can
  // act on a "not set up" prompt, so only they are shown one.
  const canManageConnections = me?.permissions.includes('connections.manage') ?? false;
  // Clients reply too — this is the one composer capability they keep, so it is gated on the
  // note permission rather than on being staff.
  const canReply = me?.permissions.includes('tickets.note.public.add') ?? false;
  const [replyInternal, setReplyInternal] = useState(false);
  // The composer lives in a dialog now; this only controls visibility, never the draft.
  const [replyOpen, setReplyOpen] = useState(false);
  const [replyStatus, setReplyStatus] = useState('');

  // Who a public reply reaches. Loaded only for staff writing a public reply — a client has no
  // business enumerating their colleagues' addresses, and an internal note reaches nobody.
  const { data: recipients } = useQuery({
    queryKey: ['ticket-recipients', id],
    queryFn: () => api.ticketRecipients(id),
    enabled: isStaff && replyOpen && !replyInternal,
    staleTime: 5 * 60_000,
    retry: false,
  });
  const [emailContact, setEmailContact] = useState(true);
  const [ccEmails, setCcEmails] = useState<string[]>([]);

  const addComment = useMutation({
    mutationFn: async (body: string) => {
      const note = await api.addComment(id, body, isStaff ? !replyInternal : undefined,
        // Recipients ride ONLY on a public reply from staff on a provider that honours them.
        isStaff && !replyInternal && recipients?.canChooseRecipients
          ? { emailContact, emailCc: ccEmails }
          : undefined);
      const sideErrors: string[] = [];
      // Upload after the reply exists, so each file carries its note id all the way to the PSA.
      for (const file of pendingFiles) {
        try { await api.uploadAttachment(id, file, note.id); }
        catch (e) { sideErrors.push(`"${file.name}" failed to upload${e instanceof Error && e.message ? ` — ${e.message}` : ''}`); }
      }
      const hrs = parseFloat(replyHours);
      if (hrs > 0) {
        // noteId links the entry to this reply, so the thread shows the hours on the reply itself.
        try { await api.logTime(id, { hours: hrs, billable: replyBillable, notes: replyTimeNotes.trim() || body, workType: workType || undefined, workRole: workRole || undefined, noteId: note.id }); }
        catch (e) { sideErrors.push(`the time entry failed${e instanceof Error && e.message ? ` — ${e.message}` : ''} (use the Log time panel to retry)`); }
      }
      if (replyStatus) {
        try { await api.updateTicketStatus(id, replyStatus); }
        catch (e) { sideErrors.push(`the status change failed${e instanceof Error && e.message ? ` — ${e.message}` : ''} (use the status buttons to retry)`); }
      }
      return { note, sideErrors };
    },
    onSuccess: ({ sideErrors }) => {
      setComment(''); setPendingFiles([]); setReplyHours(''); setReplyStatus(''); setReplyTimeNotes(''); setWorkType(''); setWorkRole('');
      // Cc is per-reply: carrying a copy list into the NEXT reply is how someone gets mailed
      // something they were never meant to see. Emailing the contact stays on, as the default.
      setCcEmails([]); setEmailContact(true);
      // Close on success only. A failed send keeps the dialog — and the text — open, because
      // dropping someone back to a closed launcher with an error elsewhere loses the reply.
      setReplyOpen(false);
      setReplySideError(sideErrors.length > 0 ? `Reply sent, but ${sideErrors.join('; ')}.` : null);
      qc.invalidateQueries({ queryKey: ['ticket', id] });
      if (parseFloat(replyHours) > 0) refreshTime();
      if (replyStatus) [['tickets'], ['team'], ['trend']].forEach((k) => qc.invalidateQueries({ queryKey: k }));
    },
  });

  const { data: entries } = useQuery({ queryKey: ['time-entries', id], queryFn: () => api.listTimeEntries(id), enabled: !!ticket && canLogTime, retry: false });

  const refreshTime = () =>
    [['time-entries', id], ['ticket', id], ['team'], ['trend']].forEach((k) => qc.invalidateQueries({ queryKey: k }));

  const statusMut = useMutation({
    mutationFn: (status: string) => api.updateTicketStatus(id, status),
    onSuccess: () => { [['ticket', id], ['tickets'], ['team'], ['trend']].forEach((k) => qc.invalidateQueries({ queryKey: k })); },
  });
  const retryEntry = useMutation({
    mutationFn: (entryId: string) => api.retryTimeEntry(id, entryId),
    onSuccess: refreshTime,
  });
  const delEntry = useMutation({
    mutationFn: (entryId: string) => api.deleteTimeEntry(id, entryId),
    onSuccess: refreshTime,
  });
  const updEntry = useMutation({
    mutationFn: (v: { entryId: string; hours: number; notes: string }) => api.updateTimeEntry(id, v.entryId, { hours: v.hours, notes: v.notes }),
    onSuccess: () => { setEditEntry(null); refreshTime(); },
  });

  async function upload(file: File) {
    setUploading(true);
    try { await api.uploadAttachment(id, file); qc.invalidateQueries({ queryKey: ['ticket', id] }); }
    catch { /* surfaced by the disabled state */ }
    finally { setUploading(false); }
  }
  async function download(attachmentId: string) {
    try { const { url } = await api.attachmentDownloadUrl(id, attachmentId); window.open(url, '_blank', 'noopener'); } catch { /* */ }
  }

  // Prev/next navigation across the ticket list (ordered newest-first by the API).
  const idx = list?.findIndex((t) => t.id === id) ?? -1;
  const prev = idx > 0 ? list?.[idx - 1] : undefined;
  const next = idx >= 0 && list ? list[idx + 1] : undefined;

  // Full width, like every other dashboard page. The old max-w-4xl centred the page in 896px and
  // left the rest of a widescreen empty — a sensible cap for one narrow column, but wasted space
  // once the properties moved into their own rail. Reading width is protected where it actually
  // matters (the conversation bubbles) rather than by starving the whole page.
  return (
    <div className="space-y-4">
      {/* Header controls */}
      <div className="flex flex-wrap items-center justify-between gap-2">
        <Link href="/dashboard/tickets" className="inline-flex items-center gap-1.5 text-sm text-[var(--muted)] hover:text-[var(--fg)]">
          <ArrowLeft size={16} /> Back to tickets
        </Link>
        <div className="flex items-center gap-2">
          <div className="relative">
            <button onClick={() => setMenuOpen((v) => !v)} onBlur={() => setTimeout(() => setMenuOpen(false), 150)}
              className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)]">
              <MoreHorizontal size={15} /> More actions <ChevronDown size={13} className="text-[var(--faint)]" />
            </button>
            {menuOpen && (
              <div className="absolute right-0 z-10 mt-1 w-48 overflow-hidden rounded-lg border border-[var(--border)] bg-[var(--surface)] py-1 text-sm shadow-lg">
                <button onClick={() => { if (ticket?.externalTicketId) navigator.clipboard?.writeText(ticket.externalTicketId); }} className="flex w-full items-center gap-2 px-3 py-2 hover:bg-[var(--bg)]"><Copy size={14} /> Copy reference</button>
                {ticket?.externalTicketUrl && (
                  <a
                    href={ticket.externalTicketUrl}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="flex w-full items-center gap-2 px-3 py-2 hover:bg-[var(--bg)]"
                  >
                    <ExternalLink size={14} /> Open in {ticket.connectionName ?? 'PSA'}
                  </a>
                )}
                <button onClick={() => qc.invalidateQueries({ queryKey: ['ticket', id] })} className="flex w-full items-center gap-2 px-3 py-2 hover:bg-[var(--bg)]"><RefreshCw size={14} /> Refresh</button>
                <Link href="/dashboard/tickets" className="flex w-full items-center gap-2 px-3 py-2 hover:bg-[var(--bg)]"><ArrowLeft size={14} /> Back to tickets</Link>
              </div>
            )}
          </div>
          <div className="flex items-center">
            <button disabled={!prev} onClick={() => prev && router.push(`/dashboard/tickets/${prev.id}`)} aria-label="Previous ticket"
              className="rounded-l-lg border border-[var(--border)] bg-[var(--surface)] p-2 text-[var(--muted)] hover:bg-[var(--bg)] disabled:opacity-40"><ChevronLeft size={16} /></button>
            <button disabled={!next} onClick={() => next && router.push(`/dashboard/tickets/${next.id}`)} aria-label="Next ticket"
              className="rounded-r-lg border border-l-0 border-[var(--border)] bg-[var(--surface)] p-2 text-[var(--muted)] hover:bg-[var(--bg)] disabled:opacity-40"><ChevronRight size={16} /></button>
          </div>
        </div>
      </div>

      {isLoading && <div className="h-48 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}
      {isError && <div className="rounded-xl border border-dashed border-[var(--border)] p-8 text-center text-sm text-[var(--muted)]">Couldn&apos;t load this ticket — is the API running?</div>}

      {ticket && (() => {
        const Icon = categoryIcon(ticket.portalCategory, ticket.title);
        // Files belong to the message they were posted with; only genuinely loose ones fall through
        // to the list at the bottom.
        const filesByNote = new Map<string, TicketAttachment[]>();
        for (const a of ticket.attachments) {
          if (!a.ticketNoteId) continue;
          const bucket = filesByNote.get(a.ticketNoteId) ?? [];
          bucket.push(a);
          filesByNote.set(a.ticketNoteId, bucket);
        }
        const looseFiles = ticket.attachments.filter((a) => !a.ticketNoteId);

        const convo = [...ticket.conversation].sort((a, b) =>
          (oldestFirst ? 1 : -1) * (new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime()));

        // Long threads open on the latest few; the rest are one click away. A sixteen-message
        // history is reference material, and scrolling past all of it to reach the current state
        // is the wrong default.
        //
        // "Latest" means most RECENT, which is not a fixed end of the list: sorted oldest-first the
        // newest messages are at the BOTTOM, newest-first they are at the TOP. Slicing the same end
        // in both would quietly show the four OLDEST in one of them.
        const hiddenNotes = Math.max(0, convo.length - RECENT_NOTE_COUNT);
        const notesCollapsed = !showAllNotes && hiddenNotes > 0;
        const visibleConvo = notesCollapsed
          ? (oldestFirst ? convo.slice(-RECENT_NOTE_COUNT) : convo.slice(0, RECENT_NOTE_COUNT))
          : convo;
        return (
          <>
            {/* Properties left, work centre. The eight-field grid used to sit full-width above
                everything, pushing the conversation — the thing being worked — below the fold. As a
                rail it is glanceable and stops competing with the thread. */}
            {/* Three columns from `lg`, not `xl`. Waiting for 1280px meant a 1366 laptop at the
                Windows default 125% scaling reports ~1090 CSS pixels and never reached it, so the
                assistant dropped below the thread on the machines it is actually used on. The
                side rails give up width at `lg` so the thread keeps a workable measure. */}
            <div className="grid gap-4 lg:grid-cols-[260px_minmax(0,1fr)_260px] xl:grid-cols-[320px_minmax(0,1fr)_320px] lg:items-start">
            {/* Both rails are given the SAME height — one screen, less the top offset — so the two
                sides of the ticket line up instead of one ending halfway up the other. Each scrolls
                inside itself rather than stretching to the thread's height, which on a sixteen
                message ticket would be several screens of empty rail. */}
            {/* Both rails size to their content and cap at one screen. Stretching them to equal
                heights only moved the empty space from one side to the other — the two panels hold
                different amounts, and padding the shorter one buys symmetry with blankness. Equal
                WIDTHS keep the page aligned; height follows what there is to show. */}
            <aside className="flex flex-col gap-4 lg:sticky lg:top-4 lg:max-h-[calc(100vh-2rem)] lg:overflow-y-auto">
            {/* Ticket card */}
            <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-5">
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div className="flex items-start gap-3">
                  <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-[var(--bg)] text-[var(--muted)]"><Icon size={19} /></span>
                  <div className="min-w-0">
                    <h1 className="text-base font-semibold leading-snug">{ticket.title}</h1>
                    <p className="whitespace-pre-line text-sm text-[var(--muted)]">{ticket.description ?? 'No description provided.'}</p>
                  </div>
                </div>
                <div className="flex items-center gap-2">
                  {canUpdate ? (
                  <span className="relative">
                    <select value={ticket.portalStatus} disabled={statusMut.isPending}
                      onChange={(e) => statusMut.mutate(e.target.value)} aria-label="Ticket status"
                      className={`cursor-pointer appearance-none rounded-md border-0 py-1 pl-2.5 pr-7 text-xs font-semibold outline-none focus:ring-2 focus:ring-brand disabled:opacity-60 ${STATUS_TONE[ticket.portalStatus] ?? STATUS_TONE.NEW}`}>
                      {!STATUSES.includes(ticket.portalStatus) && <option value={ticket.portalStatus}>{ticket.portalStatus.replace(/_/g, ' ')}</option>}
                      {STATUSES.map((s) => <option key={s} value={s}>{s.replace(/_/g, ' ')}</option>)}
                    </select>
                    <ChevronDown size={12} className="pointer-events-none absolute right-2 top-1/2 -translate-y-1/2 opacity-70" />
                  </span>
                  ) : (
                  <span className={`inline-flex items-center rounded-md px-2.5 py-1 text-xs font-semibold ${STATUS_TONE[ticket.portalStatus] ?? STATUS_TONE.NEW}`}>
                    {ticket.portalStatus.replace(/_/g, ' ')}
                  </span>
                  )}
                  <span className={`inline-flex items-center rounded-md px-2.5 py-1 text-xs font-semibold ${PRIORITY_TONE[ticket.portalPriority.toUpperCase()] ?? PRIORITY_TONE.NORMAL}`}>{ticket.portalPriority.toUpperCase()}</span>
                </div>
              </div>
              {statusMut.isError && (
                <p className="mt-2 text-right text-xs text-red-600 dark:text-red-400">
                  Couldn&apos;t change status: {statusMut.error instanceof Error ? statusMut.error.message : 'the connection is unreachable.'}
                </p>
              )}
              {/* One per row in the rail; still a responsive grid on narrow screens where the rail
                  collapses above the content. */}
              <dl className="mt-4 grid grid-cols-2 gap-x-6 gap-y-3 border-t border-[var(--border)] pt-4 text-sm sm:grid-cols-3 lg:grid-cols-1 lg:gap-y-0 lg:divide-y lg:divide-[var(--border)]">
                <Meta label="Reference" value={ticket.externalTicketId ?? '—'} href={ticket.externalTicketUrl} />
                <Meta label="Source" value={ticket.connectionName ?? '—'} />
                <Meta label="Queue / Board" value={ticket.queueOrBoard ?? '—'} />
                {/* Two lines, not one merged answer. The PSA's assignee and the person actually
                    working it are different facts, and on a desk where technicians exist only in
                    the portal the provider's line will read as the integration account or nothing
                    at all — collapsing them would report that as "unassigned" while someone is
                    mid-way through the job. */}
                <Meta label="Assigned to (PSA)" value={ticket.assignedTechnicianName ?? ticket.assignedTechnicianExternalId ?? 'Unassigned'} />
                {ticket.assignedAppUserName && <Meta label="Working it" value={ticket.assignedAppUserName} />}
                <Meta label="Category" value={ticket.portalCategory ?? '—'} />
                <Meta label="Customer" value={ticket.customerName ?? '—'} />
                <Meta label="Opened" value={fmt(ticket.createdAt)} />
                <Meta label="Updated" value={fmt(ticket.updatedAt)} />
              </dl>

              {canUpdate && (
              <div className="mt-4 border-t border-[var(--border)] pt-4">
                {!assignOpen ? (
                  <button onClick={() => setAssignOpen(true)}
                    className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-3 py-1.5 text-xs font-medium hover:bg-[var(--bg)]">
                    <UserCog size={14} /> {ticket.assignedTechnicianName || ticket.assignedAppUserName ? 'Reassign or move queue' : 'Assign technician'}
                  </button>
                ) : (
                  <AssignPanel
                    options={assignOpts}
                    currentTechnicianId={ticket.assignedTechnicianExternalId}
                    currentQueueId={assignOpts?.queueOrBoardId ?? null}
                    currentAppUserId={ticket.assignedAppUserId}
                    pending={assign.isPending}
                    error={assign.isError ? (assign.error instanceof Error ? assign.error.message : 'The PSA rejected the change.') : null}
                    onCancel={() => { setAssignOpen(false); assign.reset(); }}
                    onSave={(body) => assign.mutate(body)}
                  />
                )}
              </div>
              )}
            </div>

            {/* Service instructions the client set for technicians (from the Control Panel). */}
            {ticket.serviceInstructions && (
              <div className="rounded-xl border border-amber-200 bg-amber-50 p-5 dark:border-amber-900/60 dark:bg-amber-950/30">
                <h2 className="mb-2 flex items-center gap-2 text-sm font-semibold uppercase tracking-wide text-amber-700 dark:text-amber-300">
                  <ClipboardList size={14} /> Service Instructions
                </h2>
                <p className="whitespace-pre-wrap font-mono text-sm leading-relaxed text-amber-900 dark:text-amber-100">{ticket.serviceInstructions}</p>
                <p className="mt-2 text-xs text-amber-700/70 dark:text-amber-300/60">Set by the customer in their Control Panel — follow these when working this ticket.</p>
              </div>
            )}

            </aside>

            <div className="space-y-4">
            {/* Reply launcher — closed by default, above Time entries. A composer pinned to the
                bottom of a long thread is half off-screen exactly when it is needed.
                Styled as the primary action it is: a dashed grey outline with muted text is the
                vocabulary of an empty slot or a disabled control, so the one button a technician
                opens the ticket to press was the quietest thing on the page. Solid brand border,
                brand tint, full-strength label — and a Reply cue on the right so the target of the
                click is named rather than merely implied by a placeholder.
                The icon square and that cue switch to brand-mid on dark: the deep forest green is
                nearly the value of a dark surface, so the marks carrying the emphasis would lose
                it exactly where they are needed. */}
            {canReply && (
              <button type="button" onClick={() => setReplyOpen(true)}
                className="flex w-full items-center gap-3 rounded-xl border-2 border-brand/40 bg-brand-tint px-4 py-3.5 text-left text-sm font-medium text-[var(--fg)] shadow-sm transition-colors hover:border-brand hover:bg-brand/10 dark:border-brand/35 dark:bg-brand/10 dark:hover:bg-brand/20">
                <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-lg bg-brand text-white dark:bg-brand-mid">
                  <Pencil size={14} />
                </span>
                <span className="min-w-0 flex-1 truncate">
                  {comment.trim() || pendingFiles.length > 0
                    ? `Draft in progress — ${comment.trim() ? `“${comment.trim().slice(0, 48)}${comment.trim().length > 48 ? "…" : ""}”` : `${pendingFiles.length} file${pendingFiles.length > 1 ? "s" : ""} attached`}`
                    : isStaff ? "Add a reply, internal note, time or status change…" : "Add a reply…"}
                </span>
                {(comment.trim() || pendingFiles.length > 0) ? (
                  <span className="shrink-0 rounded-full bg-amber-100 px-2 py-0.5 text-[10px] font-semibold text-amber-700 dark:bg-amber-950 dark:text-amber-300">Draft</span>
                ) : (
                  <span className="hidden shrink-0 rounded-lg bg-brand px-2.5 py-1 text-xs font-semibold text-white dark:bg-brand-mid sm:inline">Reply</span>
                )}
              </button>
            )}


            {/* Time entries (query disabled without tickets.time.log, so this stays absent for clients) */}
            {entries && entries.length > 0 && (
              <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
                {/* The whole header is the disclosure control. A chevron alone is a small target,
                    and the totals beside it are read-only text, so there is nothing here that a
                    click could mean other than "open this". */}
                <button type="button" onClick={toggleTime}
                  aria-expanded={timeOpen} aria-controls="time-entry-list"
                  title={timeOpen ? 'Hide time entries' : `Show ${entries.length} time ${entries.length === 1 ? 'entry' : 'entries'}`}
                  className="flex w-full flex-wrap items-center justify-between gap-3 rounded-t-xl px-5 py-3 text-left hover:bg-[var(--bg)]">
                  <h2 className="flex items-center gap-2 text-sm font-semibold uppercase tracking-wide text-[var(--faint)]">
                    <ChevronDown size={14} aria-hidden
                      className={`shrink-0 transition-transform duration-150 ${timeOpen ? '' : '-rotate-90'}`} />
                    <Clock size={14} /> Time entries <span className="rounded-full bg-[var(--bg)] px-2 py-0.5 text-xs text-[var(--muted)]">{entries.length}</span>
                  </h2>
                  {(() => {
                    // Totals count only time the PSA actually holds, so this figure reconciles with
                    // the provider's own summary. Rejected entries are called out separately rather
                    // than folded in, where they would silently inflate what the customer is shown.
                    const synced = entries.filter((e) => e.syncStatus === 'Synced');
                    const failed = entries.filter((e) => e.syncStatus !== 'Synced');
                    const total = synced.reduce((a, e) => a + e.hours, 0);
                    const billable = synced.filter((e) => e.billable).reduce((a, e) => a + e.hours, 0);
                    // Figures, not a sentence: three labelled numbers scan in a glance where a
                    // run-on line of "Total: … (0.0000 h) · billable …" did not. The 4-decimal raw
                    // value moves to a tooltip — it is reconciliation detail, not headline data.
                    const Stat = ({ label, value, tone = '' }: { label: string; value: string; tone?: string }) => (
                      <span className="flex flex-col items-end leading-tight">
                        <span className={`text-sm font-semibold tabular-nums ${tone}`}>{value}</span>
                        <span className="text-[10px] uppercase tracking-wide text-[var(--faint)]">{label}</span>
                      </span>
                    );
                    return (
                      <div className="flex items-center gap-4" title={`${total.toFixed(4)} hours recorded`}>
                        <Stat label="Recorded" value={fmtDuration(total)} />
                        <Stat label="Billable" value={fmtDuration(billable)} tone="text-green-700 dark:text-green-400" />
                        {failed.length > 0 && (
                          <Stat label={`Not recorded (${failed.length})`}
                            value={fmtDuration(failed.reduce((a, e) => a + e.hours, 0))}
                            tone="text-red-600 dark:text-red-400" />
                        )}
                      </div>
                    );
                  })()}
                </button>
                <ul id="time-entry-list" hidden={!timeOpen}
                  className="divide-y divide-[var(--border)] border-t border-[var(--border)]">
                  {entries.map((e) => (
                    <li key={e.externalId} className="px-5 py-3">
                      {editEntry?.id === e.externalId ? (
                        <div className="flex flex-wrap items-center gap-2">
                          <input type="number" step="0.01" min="0" value={editEntry.hours}
                            onChange={(ev) => setEditEntry({ ...editEntry, hours: ev.target.value })}
                            className="w-20 rounded-md border border-brand bg-[var(--bg)] px-2 py-1 text-sm outline-none" />
                          <input value={editEntry.notes} onChange={(ev) => setEditEntry({ ...editEntry, notes: ev.target.value })}
                            placeholder="Notes" className="min-w-40 flex-1 rounded-md border border-[var(--border)] bg-[var(--bg)] px-2 py-1 text-sm outline-none focus:border-brand" />
                          <button onClick={() => { const h = parseFloat(editEntry.hours); if (h > 0) updEntry.mutate({ entryId: e.externalId, hours: h, notes: editEntry.notes }); }}
                            disabled={updEntry.isPending} className="rounded-md border border-[var(--border)] p-1.5 text-green-600 hover:bg-[var(--bg)]"><Check size={15} /></button>
                          <button onClick={() => setEditEntry(null)} className="rounded-md border border-[var(--border)] p-1.5 text-[var(--muted)] hover:bg-[var(--bg)]"><X size={15} /></button>
                        </div>
                      ) : (
                        // Two rows, not one: the old layout raced ten elements along a single line
                        // and hid half of them below `lg`, so on a laptop the technician, the source
                        // and the sync state simply vanished. Duration leads, meta follows, and the
                        // note gets the full width it needs.
                        <div className="group">
                          <div className="flex items-start gap-3">
                            <span className="flex w-16 shrink-0 flex-col items-start" title={`${e.hours.toFixed(4)} hours`}>
                              <strong className="text-[15px] leading-tight tabular-nums">{fmtDuration(e.hours)}</strong>
                            </span>

                            <div className="min-w-0 flex-1">
                              <div className="flex flex-wrap items-center gap-1.5">
                                <span className={`rounded px-1.5 py-0.5 text-[11px] font-medium ${e.billable ? 'bg-green-100 text-green-700 dark:bg-green-950 dark:text-green-300' : 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300'}`}>{billableLabel(e.billableOption, e.billable)}</span>
                                {e.workType && <span className="rounded bg-[var(--bg)] px-1.5 py-0.5 text-[11px] text-[var(--muted)]">{e.workType}</span>}
                                {e.technicianName && <span className="text-xs text-[var(--muted)]">{e.technicianName}</span>}
                                <span className="text-xs text-[var(--faint)]">·</span>
                                <span className="text-xs text-[var(--faint)]">{fmtDay(e.entryDate)}</span>
                                {/* Where it was logged and whether the PSA holds it — one statement
                                    rather than two chips saying overlapping things. */}
                                <span className={`rounded px-1.5 py-0.5 text-[11px] font-medium ${e.syncStatus !== 'Synced'
                                  ? 'bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300'
                                  : e.source === 'Portal' ? 'bg-brand/10 text-brand' : 'bg-blue-100 text-blue-700 dark:bg-blue-950 dark:text-blue-300'}`}
                                  title={e.syncStatus === 'Synced' ? `${providerLabel(Number(ticket.provider))} #${e.externalId}` : undefined}>
                                  {e.syncStatus !== 'Synced'
                                    ? `Not in ${providerAbbrev(Number(ticket.provider))}`
                                    : e.source === 'Portal' ? `Portal → ${providerAbbrev(Number(ticket.provider))}` : providerLabel(Number(ticket.provider))}
                                </span>
                              </div>

                              {/* The note gets its own line at full width instead of being squeezed
                                  between chips and truncated to nothing. */}
                              {expandedNotes.has(e.externalId) ? (
                                <div className="mt-1">
                                  <NoteBody body={e.notes ?? ''} />
                                  <button type="button"
                                    onClick={() => setExpandedNotes((p) => { const n = new Set(p); n.delete(e.externalId); return n; })}
                                    className="mt-1 text-xs font-medium text-brand hover:underline">
                                    Collapse notes
                                  </button>
                                </div>
                              ) : e.notes ? (
                                <button type="button"
                                  onClick={() => setExpandedNotes((p) => { const n = new Set(p); n.add(e.externalId); return n; })}
                                  title="Show full notes"
                                  className="mt-1 block w-full truncate text-left text-sm text-[var(--muted)] hover:text-[var(--fg)]">
                                  {notePreview(e.notes)}
                                </button>
                              ) : (
                                <span className="mt-1 block text-sm text-[var(--faint)]">No notes</span>
                              )}
                            </div>

                            {/* Actions stay quiet until the row is hovered or focused, so a list of
                                entries reads as data rather than a wall of buttons. */}
                            <div className="flex shrink-0 gap-0.5 opacity-60 transition-opacity focus-within:opacity-100 group-hover:opacity-100">
                              {/* A rejected entry has no provider counterpart to edit. It can be sent
                                  again once the cause is fixed, or discarded — leaving it with no
                                  actions at all stranded the work on screen permanently. */}
                              {e.syncStatus === 'Synced' ? (
                                <>
                                  <button onClick={() => setEditEntry({ id: e.externalId, hours: e.hours.toString(), notes: e.notes ?? '' })}
                                    aria-label="Edit" title="Edit" className="rounded-md p-1.5 text-[var(--muted)] hover:bg-[var(--bg)] hover:text-brand"><Pencil size={14} /></button>
                                  <button onClick={() => { if (window.confirm('Delete this time entry from the PSA?')) delEntry.mutate(e.externalId); }}
                                    disabled={delEntry.isPending} aria-label="Delete" title="Delete" className="rounded-md p-1.5 text-[var(--muted)] hover:bg-red-50 hover:text-red-600 dark:hover:bg-red-950/50"><Trash2 size={14} /></button>
                                </>
                              ) : (
                                <>
                                  <button onClick={() => retryEntry.mutate(e.externalId)} disabled={retryEntry.isPending}
                                    aria-label="Send to PSA again" title="Send to the PSA again"
                                    className="rounded-md p-1.5 text-[var(--muted)] hover:bg-[var(--bg)] hover:text-brand disabled:opacity-50"><RefreshCw size={14} /></button>
                                  <button onClick={() => { if (window.confirm('Discard this entry? It was never recorded in the PSA.')) delEntry.mutate(e.externalId); }}
                                    disabled={delEntry.isPending} aria-label="Discard" title="Discard"
                                    className="rounded-md p-1.5 text-[var(--muted)] hover:bg-red-50 hover:text-red-600 dark:hover:bg-red-950/50"><Trash2 size={14} /></button>
                                </>
                              )}
                            </div>
                          </div>

                          {/* A failure is a callout, not a floating red sentence nudged into place
                              with a padding hack that broke the moment the layout changed. */}
                          {e.syncStatus !== 'Synced' && (
                            <div className="mt-2 flex gap-2 rounded-lg border border-red-200 bg-red-50 px-3 py-2 dark:border-red-900/60 dark:bg-red-950/30">
                              <AlertTriangle size={14} className="mt-0.5 shrink-0 text-red-600 dark:text-red-400" />
                              <p className="min-w-0 text-xs leading-relaxed text-red-700 dark:text-red-300">
                                <span className="font-medium">Not recorded in {providerLabel(Number(ticket.provider))}.</span>{' '}
                                {e.syncError}{' '}
                                {/* The retry's OWN message, not a generic "still rejected": a retry
                                    usually fails for a different reason than the original, and hiding
                                    it left the stale reason on screen as if nothing had changed. */}
                                {retryEntry.isError
                                  ? <span className="text-red-600/80 dark:text-red-400/80">
                                      Retry rejected{retryEntry.error instanceof Error && retryEntry.error.message ? `: ${retryEntry.error.message}` : ''} — fix the cause, then send again.
                                    </span>
                                  : <span className="text-red-600/80 dark:text-red-400/80">Fix the cause, then send again.</span>}
                              </p>
                            </div>
                          )}
                        </div>
                      )}
                    </li>
                  ))}
                </ul>
              </div>
            )}

            {/* Conversation */}
            <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
              <div className="flex items-center justify-between border-b border-[var(--border)] px-5 py-3">
                <h2 className="flex items-center gap-2 text-sm font-semibold uppercase tracking-wide text-[var(--faint)]">
                  Conversation <span className="rounded-full bg-[var(--bg)] px-2 py-0.5 text-xs text-[var(--muted)]">{convo.length}</span>
                </h2>
                <button onClick={() => setOldestFirst((v) => !v)} className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)]">
                  <ArrowUpDown size={13} /> {oldestFirst ? 'Oldest first' : 'Newest first'} <ChevronDown size={12} />
                </button>
              </div>

              <div className="space-y-4 px-5 py-4">
                {convo.length === 0 && <p className="rounded-lg border border-dashed border-[var(--border)] p-4 text-sm text-[var(--muted)]">No public replies yet.</p>}
                {visibleConvo.map((n, i) => {
                  // Chat layout: the client's messages sit LEFT, everything from the MSP side sits
                  // RIGHT — direction is readable before a single word is. Coloring stays semantic,
                  // not per-author rainbow: client neutral, staff replies brand-tinted, internal
                  // notes amber, internal time entries blue.
                  const incoming = n.authoredByClient;
                  const isTimeCard = !!n.timeEntryExternalId && !n.isPublic;
                  // The fill carries the meaning, so it is a real tint rather than a wash: at 5%
                  // opacity the staff and internal cards were nearly indistinguishable from the
                  // client's. Coloured cards take a border the same colour as the fill — a
                  // contrasting outline around an already-tinted card just adds noise — while the
                  // neutral client card keeps a visible edge so it does not float.
                  const tone = isTimeCard
                    ? 'border-blue-100 bg-blue-50 dark:border-blue-950 dark:bg-blue-950/40'
                    : !n.isPublic
                      ? 'border-amber-100 bg-amber-100/70 dark:border-amber-950 dark:bg-amber-950/40'
                      : incoming
                        ? 'border-[var(--border)] bg-[var(--bg)]'
                        : 'border-brand-tint bg-brand-tint dark:border-brand/25 dark:bg-brand/15';
                  // The time this note's work took, from the live entry when loaded, else from the
                  // note itself (portal-logged replies carry their hours) — never fabricated.
                  const te = n.timeEntryExternalId ? entries?.find((e) => e.externalId === n.timeEntryExternalId) : undefined;
                  const teHours = te?.hours ?? n.timeEntryHours;
                  const teBillable = te ? billableLabel(te.billableOption, te.billable)
                    : n.timeEntryBillable == null ? null : n.timeEntryBillable ? 'Billable' : 'Do not bill';
                  // The rail only bridges to the NEXT message when it is from the same side, so
                  // each burst of consecutive messages reads as one connected thread segment.
                  // Looks ahead in what is RENDERED, not the full thread: keyed off `convo` the last
                  // visible message would grow a rail bridging to something that is not on screen.
                  const nextSameSide = visibleConvo[i + 1] !== undefined && visibleConvo[i + 1].authoredByClient === n.authoredByClient;
                  return (
                  <div key={n.id} className={`flex gap-3 ${incoming ? '' : 'flex-row-reverse'}`}>
                    {/* Identity column: who spoke, stated once beside the bubble — avatar, name,
                        side — with a timeline rail linking consecutive messages from this side. */}
                    <div className="relative flex w-24 shrink-0 flex-col items-center gap-1 pt-1">
                      {nextSameSide && (
                        <span aria-hidden className={`absolute -bottom-4 top-14 w-px ${incoming ? 'bg-blue-200 dark:bg-blue-900/60' : 'bg-brand/25'}`} />
                      )}
                      <span className={`relative z-10 flex h-11 w-11 items-center justify-center rounded-full text-sm font-semibold ${incoming ? 'bg-blue-600 text-white' : 'bg-brand text-brand-fg'}`}>{initials(n.authorName)}</span>
                      <span className="max-w-full truncate text-center text-xs font-medium leading-tight">{n.authorName}</span>
                      <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${incoming ? 'bg-slate-200/70 text-slate-600 dark:bg-slate-800 dark:text-slate-300' : 'bg-green-100 text-green-700 dark:bg-green-950 dark:text-green-300'}`}>{incoming ? 'Client' : 'Technician'}</span>
                    </div>
                    {/* flex-1 + max-w: every card the same width, edges aligned — content-sized
                        cards gave the thread a ragged, unprofessional left edge. The rotated square
                        is the speech-bubble tail, pointing at the author's identity column. */}
                    {/* 85% on a narrow screen, but never wider than a comfortable measure: prose
                        set 1500px across is a line the eye loses its place on. */}
                    <div className={`relative min-w-0 max-w-[min(85%,78ch)] flex-1 rounded-lg border p-3 before:absolute before:top-5 before:h-3 before:w-3 before:rotate-45 before:border-inherit before:bg-inherit ${incoming ? 'before:-left-[6.5px] before:border-b before:border-l' : 'before:-right-[6.5px] before:border-r before:border-t'} ${tone}`}>
                      <div className="flex items-center justify-between gap-4">
                        <span className="flex flex-wrap items-center gap-2 text-sm">
                          {/* The name is the loudest thing in the header, so it stays ink: the card
                              and the badge already say which side spoke, and colouring it too made
                              three signals compete for the same fact. */}
                          <span className="font-semibold">{n.authorName}</span>
                          <span className={`rounded px-1.5 py-0.5 text-[11px] font-medium ${incoming ? 'bg-slate-200/70 text-slate-600 dark:bg-slate-800 dark:text-slate-300' : 'bg-green-100 text-green-700 dark:bg-green-950 dark:text-green-300'}`}>{incoming ? 'Client' : 'Technician'}</span>
                          {!n.isPublic && !isTimeCard && (
                            <span title="Internal note from the PSA — never shown to the client"
                              className="rounded bg-slate-200/70 px-1.5 py-0.5 text-[11px] font-medium text-slate-600 dark:bg-slate-800 dark:text-slate-300">Internal</span>
                          )}
                          {isTimeCard && (
                            <span title={`Time entry #${n.timeEntryExternalId} — internal, never shown to the client`}
                              className="rounded bg-blue-100 px-1.5 py-0.5 text-[11px] font-medium text-blue-700 dark:bg-blue-950 dark:text-blue-300">Time entry</span>
                          )}
                          {/* Duration sits WITH the badge that introduces it, not on a line of its
                              own below. "Time entry" and "23m · Billable" are one statement about
                              this note; splitting them spent a whole row restating in prose what the
                              header had already begun, and pushed the note itself further down. */}
                          {n.timeEntryExternalId && (
                            <span className="inline-flex items-center gap-1 text-[13px] font-semibold text-blue-700 dark:text-blue-300">
                              <Clock size={13} />
                              {teHours != null ? fmtDuration(teHours) : 'Time logged'}{teBillable ? ` · ${teBillable}` : ''}
                            </span>
                          )}
                        </span>
                        <span className="shrink-0 text-xs text-[var(--faint)]">{fmt(n.createdAt, true)}</span>
                      </div>
                      <NoteBody body={n.body} />
                      {filesByNote.get(n.id)?.length ? (
                        <ul className="mt-2 flex flex-wrap gap-2">
                          {filesByNote.get(n.id)!.map((a) => (
                            <li key={a.id}>
                              <AttachmentChip a={a} provider={Number(ticket.provider)} ticketId={id} onDownload={download} />
                            </li>
                          ))}
                        </ul>
                      ) : null}
                    </div>
                  </div>
                  );
                })}

                {/* At the foot of the thread in both sort orders. What is hidden is always the
                    OLDEST part of the history, so "earlier" is accurate either way — and it says
                    what you get rather than making you count. */}
                {hiddenNotes > 0 && (
                  <button type="button" onClick={() => setShowAllNotes((v) => !v)}
                    aria-expanded={showAllNotes}
                    className="flex w-full items-center justify-center gap-2 rounded-lg border border-dashed border-[var(--border)] px-4 py-2.5 text-sm font-medium text-[var(--muted)] hover:border-brand hover:bg-brand/5 hover:text-brand">
                    <ChevronDown size={14} aria-hidden className={showAllNotes ? 'rotate-180' : ''} />
                    {showAllNotes
                      ? `Show latest ${RECENT_NOTE_COUNT} only`
                      : `Show ${hiddenNotes} earlier ${hiddenNotes === 1 ? 'note' : 'notes'}`}
                  </button>
                )}
              </div>
            </div>

            {/* Attachments */}
            <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-5">
              <h2 className="mb-3 flex items-center gap-2 text-sm font-semibold uppercase tracking-wide text-[var(--faint)]">
                Other files <span className="rounded-full bg-[var(--bg)] px-2 py-0.5 text-xs text-[var(--muted)]">{looseFiles.length}</span>
              </h2>
              <p className="-mt-2 mb-3 text-xs text-[var(--muted)]">
                Files not posted with a reply. Anything attached to a message is shown with it above.
              </p>
              {looseFiles.length > 0 && (
                <ul className="mb-3 space-y-2">
                  {looseFiles.map((a) => {
                    const clean = String(a.scanStatus) === '1' || String(a.scanStatus) === 'Clean';
                    const sourceLabel = providerLabel(Number(ticket.provider));

                    // Same rule as the thread: an image shows itself, and carries its own download.
                    // The row's name, size and source go with it - a picture you can see does not
                    // need to be described, and the two lists reading differently was the oddity.
                    if (clean && isPreviewableImage(a.contentType)) {
                      return (
                        <li key={a.id}>
                          <AttachmentPreview
                            ticketId={id} attachmentId={a.id} fileName={a.fileName}
                            contentType={a.contentType} clean={clean}
                            onDownload={() => download(a.id)}
                          />
                        </li>
                      );
                    }

                    return (
                      <li key={a.id} className="rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm">
                      <div className="flex items-center gap-2">
                        <Paperclip size={15} className="text-[var(--muted)]" />
                        <span className="truncate">{a.fileName}</span>
                        {a.fromProvider && (
                          <span title={a.authorName ? `Attached by ${a.authorName}` : undefined}
                            className="shrink-0 rounded bg-[var(--bg)] px-1.5 py-0.5 text-[11px] text-[var(--muted)]">
                            From {sourceLabel}
                          </span>
                        )}
                        {!clean && <span className="rounded bg-red-100 px-1.5 py-0.5 text-xs font-medium text-red-700 dark:bg-red-950 dark:text-red-300">Quarantined</span>}
                        <span className="ml-auto text-xs text-[var(--muted)]">{fmtSize(a.sizeBytes)}</span>
                        {clean && <button onClick={() => download(a.id)} className="rounded p-1 text-[var(--muted)] hover:text-brand"><Download size={15} /></button>}
                        </div>
                      </li>
                    );
                  })}
                </ul>
              )}
              <div
                onDragOver={(e) => { e.preventDefault(); setDragOver(true); }}
                onDragLeave={() => setDragOver(false)}
                onDrop={(e) => { e.preventDefault(); setDragOver(false); const f = e.dataTransfer.files?.[0]; if (f) upload(f); }}
                onClick={() => fileRef.current?.click()}
                className={`flex cursor-pointer flex-col items-center rounded-xl border border-dashed px-6 py-8 text-center transition-colors ${dragOver ? 'border-brand bg-brand/5' : 'border-[var(--border)] hover:bg-[var(--bg)]'}`}>
                <Paperclip size={20} className="mb-2 text-[var(--faint)]" />
                <span className="text-sm font-medium">{uploading ? 'Uploading…' : 'Attach a file'}</span>
                <span className="text-xs text-[var(--muted)]">or drag and drop files here</span>
              </div>
              <p className="mt-2 text-xs text-[var(--faint)]">Files are scanned for malware; executables are blocked. Max 25 MB per file.</p>
            </div>

            <input ref={fileRef} type="file" multiple className="hidden" disabled={uploading}
              onChange={(e) => {
                const chosen = Array.from(e.target.files ?? []);
                // Staged, not sent: a file picked in the composer belongs to the reply being written.
                if (chosen.length) setPendingFiles((prev) => [...prev, ...chosen]);
                e.target.value = '';
              }} />
            </div>

            {/* Assistant — the grid's THIRD column, a sibling of the properties rail and the work
                centre. It used to sit INSIDE the conversation card, which is why it rendered under
                the thread and could never reach the right-hand rail however wide the window got:
                an element nested two levels down cannot become a column of the grid above it.
                Renders nothing unless the organization has switched it on. */}
            {isStaff && (
              <div className="lg:sticky lg:top-4">
                <AssistantRail
                  ticketId={id}
                  draft={comment}
                  onUseDraft={(text) => { setComment(text); setReplyOpen(true); }}
                  canConfigure={canManageConnections}
                />
              </div>
            )}
            </div>
            {/* Reply dialog. The composer keeps every capability it had inline — public/internal,
                hours, status, attachments — but a dialog gives it the focus a long thread denied it.
                Closing NEVER discards: the draft stays and the launcher advertises it. */}
            {canReply && replyOpen && (
              <div className="fixed inset-0 z-50 flex items-start justify-center overflow-y-auto bg-black/40 p-4 backdrop-blur-[1px]"
                role="dialog" aria-modal="true" aria-label="Reply to ticket"
                onClick={(e) => { if (e.target === e.currentTarget) setReplyOpen(false); }}>
                <div className="mt-8 w-full max-w-2xl rounded-xl border border-[var(--border)] bg-[var(--surface)] shadow-xl">
                  <div className="flex items-center gap-3 border-b border-[var(--border)] px-5 py-3">
                    <h2 className="text-sm font-semibold">
                      {replyInternal ? 'Internal note' : 'Reply'} · {ticket.title}
                    </h2>
                    <span className="ml-auto text-xs text-[var(--faint)]">{ticket.externalTicketId ? `#${ticket.externalTicketId}` : ''}</span>
                    <button type="button" onClick={() => setReplyOpen(false)} aria-label="Close"
                      className="rounded-md p-1 text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)]"><X size={16} /></button>
                  </div>
                  <div className="p-4">
                {/* Composer */}
                {/* No formatting toolbar: the Bold/emoji/link buttons it used to show were wired to
                  nothing — decoration presented as function. Attach lives on the real button below. */}
                <form onSubmit={(e) => { e.preventDefault(); if (comment.trim()) addComment.mutate(comment.trim()); }} className="rounded-xl border border-[var(--border)] bg-[var(--bg)]">
                {/* Recipients, above the body as every mail client puts them. Public replies only:
                    an internal note reaches nobody, so showing addresses there would invite exactly
                    the mistake this portal must never make. Every name here is a contact of THIS
                    ticket's customer, read from the PSA — the API re-checks that on send, so a
                    tampered request is refused rather than filtered. */}
                {isStaff && !replyInternal && recipients && (
                  <div className="space-y-1.5 border-b border-[var(--border)] px-4 py-2.5 text-sm">
                    {!recipients.canChooseRecipients ? (
                      // The provider owns the decision. Say what it will do rather than offer a
                      // control that quietly does nothing.
                      <p className="flex items-start gap-2 text-xs leading-relaxed text-[var(--muted)]">
                        <Mail size={14} className="mt-0.5 shrink-0" />
                        <span>
                          {providerLabel(Number(ticket.provider))} sends this reply to the ticket
                          contact at <strong className="font-medium text-[var(--fg)]">{recipients.companyName}</strong>,
                          following its own notification rules. Recipients cannot be chosen here.
                        </span>
                      </p>
                    ) : (
                      <>
                        <div className="flex items-baseline gap-3">
                          <span className="w-8 shrink-0 text-xs font-medium text-[var(--faint)]">To</span>
                          <label className="inline-flex cursor-pointer items-center gap-2 text-[13px]">
                            <input type="checkbox" checked={emailContact} onChange={(e) => setEmailContact(e.target.checked)}
                              className="h-3.5 w-3.5 accent-[color:var(--brand,#14532D)]" />
                            <span>Ticket contact at <strong className="font-medium">{recipients.companyName}</strong></span>
                          </label>
                        </div>
                        <div className="flex items-baseline gap-3">
                          <span className="w-8 shrink-0 text-xs font-medium text-[var(--faint)]">Cc</span>
                          <div className="flex min-w-0 flex-wrap gap-1.5">
                            {recipients.contacts.length === 0 && (
                              <span className="text-xs text-[var(--faint)]">No other contacts on this customer.</span>
                            )}
                            {recipients.contacts.map((c) => {
                              const on = ccEmails.includes(c.email);
                              return (
                                <button key={c.externalId} type="button"
                                  aria-pressed={on}
                                  title={c.email}
                                  onClick={() => setCcEmails((prev) => on ? prev.filter((e) => e !== c.email) : [...prev, c.email])}
                                  className={`rounded-full border px-2.5 py-0.5 text-xs transition-colors ${on
                                    ? 'border-brand bg-brand-tint text-[var(--fg)] dark:bg-brand/20'
                                    : 'border-[var(--border)] text-[var(--muted)] hover:border-brand hover:text-[var(--fg)]'}`}>
                                  {c.name || c.email}
                                </button>
                              );
                            })}
                          </div>
                        </div>
                        {!emailContact && ccEmails.length === 0 && (
                          <p className="pl-11 text-xs text-amber-700 dark:text-amber-300">
                            Nobody is being emailed — the reply is still posted to the ticket.
                          </p>
                        )}
                      </>
                    )}
                  </div>
                )}
                <textarea value={comment} onChange={(e) => setComment(e.target.value)} rows={3} maxLength={4000}
                  onKeyDown={(e) => {
                    if ((e.ctrlKey || e.metaKey) && e.key === 'Enter' && comment.trim() && !addComment.isPending) {
                      e.preventDefault();
                      addComment.mutate(comment.trim());
                    }
                  }}
                  placeholder={replyInternal ? 'Add an internal note — the client will not see this…' : 'Add a reply…'}
                  className="w-full resize-y bg-transparent px-4 py-3 text-sm outline-none" />
                {pendingFiles.length > 0 && (
                  <ul className="flex flex-wrap gap-2 px-4 pb-2">
                    {pendingFiles.map((f, i) => (
                      <li key={`${f.name}-${i}`} className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] bg-[var(--surface)] px-2 py-1 text-xs">
                        <Paperclip size={12} className="text-[var(--muted)]" />
                        <span className="max-w-40 truncate">{f.name}</span>
                        <span className="text-[var(--faint)]">{fmtSize(f.size)}</span>
                        <button type="button" aria-label={`Remove ${f.name}`}
                          onClick={() => setPendingFiles((prev) => prev.filter((_, j) => j !== i))}
                          className="text-[var(--muted)] hover:text-red-600"><X size={12} /></button>
                      </li>
                    ))}
                  </ul>
                )}
                {isStaff && (
                  <div className="flex flex-wrap items-center gap-2 border-t border-[var(--border)] px-4 py-2">
                    <div className="flex overflow-hidden rounded-lg border border-[var(--border)] text-xs font-medium">
                      <button type="button" onClick={() => setReplyInternal(false)}
                        className={`px-2.5 py-1 ${!replyInternal ? 'bg-brand text-brand-fg' : 'bg-[var(--surface)] text-[var(--muted)] hover:text-[var(--fg)]'}`}>
                        Public reply
                      </button>
                      <button type="button" onClick={() => setReplyInternal(true)}
                        title="Visible to your team and pushed to the PSA as an internal note — never shown to the client"
                        className={`px-2.5 py-1 ${replyInternal ? 'bg-amber-500 text-white' : 'bg-[var(--surface)] text-[var(--muted)] hover:text-[var(--fg)]'}`}>
                        Internal note
                      </button>
                    </div>
                    <span className="min-w-0 flex-1" />
                    <label className="flex items-center gap-1.5 text-xs text-[var(--muted)]">
                      Set status
                      <select value={replyStatus} onChange={(e) => setReplyStatus(e.target.value)} aria-label="Set status with this reply"
                        className="rounded-lg border border-[var(--border)] bg-[var(--surface)] px-2 py-1 text-xs outline-none focus:border-brand">
                        <option value="">Keep {ticket.portalStatus.replace(/_/g, ' ')}</option>
                        {STATUSES.filter((st) => st !== ticket.portalStatus).map((st) => (
                          <option key={st} value={st}>{st.replace(/_/g, ' ')}</option>
                        ))}
                      </select>
                    </label>
                  </div>
                )}
                {canLogTime && (
                <div className="flex flex-wrap items-center gap-2 border-t border-[var(--border)] px-4 py-2">
                  <Clock size={13} className="text-[var(--faint)]" />
                  <span className="text-xs text-[var(--muted)]">Log time with this reply</span>
                  <input type="number" step="0.01" min="0" value={replyHours} onChange={(e) => setReplyHours(e.target.value)}
                    placeholder="0.00" aria-label="Hours to log with this reply"
                    className="w-20 rounded-lg border border-[var(--border)] bg-[var(--surface)] px-2 py-1 text-xs outline-none focus:border-brand" />
                  {parseFloat(replyHours) > 0 && (
                    <select value={replyBillable} onChange={(e) => setReplyBillable(e.target.value)} aria-label="Billable"
                      className="rounded-lg border border-[var(--border)] bg-[var(--surface)] px-2 py-1 text-xs outline-none focus:border-brand">
                      <option value="Billable">Billable</option>
                      <option value="DoNotBill">Do not bill</option>
                      <option value="NoCharge">No charge</option>
                    </select>
                  )}
                  {/* Both halves of the timer live here now: starting one was only possible from the
                      Log time panel, so removing that panel would have taken the timer with it. */}
                  {timer.seconds > 0 && timer.target?.ticketId === id ? (
                    <button type="button"
                      onClick={() => {
                        const rounded = Math.max(0.25, Math.round((timer.seconds / 3600) / 0.25) * 0.25);
                        setReplyHours(rounded.toFixed(2));
                        timer.pause();
                      }}
                      className="rounded-lg border border-[var(--border)] bg-[var(--surface)] px-2 py-1 text-[11px] font-medium hover:bg-[var(--bg)]">
                      Use timer ({String(Math.floor(timer.seconds / 60)).padStart(2, '0')}:{String(timer.seconds % 60).padStart(2, '0')})
                    </button>
                  ) : (
                    <button type="button" onClick={startTimerHere}
                      className={`rounded-lg border px-2 py-1 text-[11px] font-medium ${
                        timer.running && timer.target?.ticketId === id
                          ? 'border-brand/40 bg-brand/5 text-brand'
                          : 'border-[var(--border)] bg-[var(--surface)] hover:bg-[var(--bg)]'}`}>
                      {timer.running && timer.target?.ticketId === id ? 'Timing…' : 'Start timer'}
                    </button>
                  )}
                  {!(parseFloat(replyHours) > 0) && (
                    <span className="text-[11px] text-[var(--faint)]">optional</span>
                  )}
                </div>
                )}
                {/* Only once there is time to describe. What the client reads and what the timesheet
                    records are not the same sentence — "your mailbox is working again" is the reply,
                    "rebuilt the OST and re-ran autodiscover" is the entry. Left blank the reply text
                    is used, which is what happened implicitly before. */}
                {canLogTime && parseFloat(replyHours) > 0 && (
                  <label className="block px-4 pb-2">
                    <span className="mb-1 block text-xs text-[var(--muted)]">
                      Time entry notes {notesRequired && <span className="text-red-600 dark:text-red-400">*</span>}
                    </span>
                    <input value={replyTimeNotes} onChange={(e) => setReplyTimeNotes(e.target.value)}
                      placeholder={comment.trim() ? 'Leave blank to use the reply text' : 'What did you work on?'}
                      className="w-full rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm outline-none focus:border-brand" />
                    <span className="mt-1 block text-[11px] text-[var(--faint)]">
                      {notesRequired
                        ? 'Autotask requires notes on every time entry — the reply text is used if you leave this blank.'
                        : 'Only the time entry sees this. The client sees the reply above.'}
                    </span>
                  </label>
                )}
                {/* Work type and role classify the entry for billing. They only existed on the Log
                    time panel, so they move here rather than disappear with it — and only when
                    the connection actually discovered options to choose from. */}
                {canLogTime && parseFloat(replyHours) > 0
                  && ((timeOpts?.workTypes.length ?? 0) > 0 || (timeOpts?.workRoles.length ?? 0) > 0) && (
                  <div className="grid grid-cols-2 gap-3 px-4 pb-2">
                    {(timeOpts?.workTypes.length ?? 0) > 0 && (
                      <label className="block">
                        <span className="mb-1 block text-xs text-[var(--muted)]">Work type</span>
                        <select value={workType} onChange={(e) => setWorkType(e.target.value)}
                          className="w-full rounded-lg border border-[var(--border)] bg-[var(--surface)] px-2 py-1.5 text-xs outline-none focus:border-brand">
                          <option value="">—</option>
                          {timeOpts!.workTypes.map((o) => <option key={o.value} value={o.label}>{o.label}</option>)}
                        </select>
                      </label>
                    )}
                    {(timeOpts?.workRoles.length ?? 0) > 0 && (
                      <label className="block">
                        <span className="mb-1 block text-xs text-[var(--muted)]">Work role</span>
                        <select value={workRole} onChange={(e) => setWorkRole(e.target.value)}
                          className="w-full rounded-lg border border-[var(--border)] bg-[var(--surface)] px-2 py-1.5 text-xs outline-none focus:border-brand">
                          <option value="">—</option>
                          {timeOpts!.workRoles.map((o) => <option key={o.value} value={o.label}>{o.label}</option>)}
                        </select>
                      </label>
                    )}
                  </div>
                )}
                <div className="flex items-center justify-between px-4 pb-3 pt-2">
                  <span className="text-xs text-[var(--faint)]">{comment.length} / 4000</span>
                  <div className="flex items-center gap-2">
                    <button type="button" onClick={() => fileRef.current?.click()} disabled={uploading}
                      className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm font-medium hover:bg-[var(--bg)] disabled:opacity-50">
                      <Paperclip size={15} /> Attach file
                    </button>
                    <button type="submit" disabled={addComment.isPending || !comment.trim()} title="Ctrl+Enter"
                      className="inline-flex items-center gap-2 rounded-lg bg-brand px-4 py-2 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
                      <Send size={15} /> {addComment.isPending ? 'Sending…'
                        : [
                            replyInternal ? 'Post internal note' : 'Send reply',
                            pendingFiles.length > 0 ? `${pendingFiles.length} file${pendingFiles.length > 1 ? 's' : ''}` : null,
                            parseFloat(replyHours) > 0 ? `${replyHours}h` : null,
                            replyStatus ? replyStatus.replace(/_/g, ' ') : null,
                          ].filter(Boolean).join(' + ')}
                    </button>
                  </div>
                </div>
                {addComment.isError && (
                  <p className="px-4 pb-3 text-xs text-red-600 dark:text-red-400">
                    Couldn&apos;t send{addComment.error instanceof Error && addComment.error.message ? ` — ${addComment.error.message}` : ' — is the API reachable?'}
                  </p>
                )}
                {replySideError && !addComment.isPending && (
                  <p className="px-4 pb-3 text-xs text-amber-700 dark:text-amber-300">{replySideError}</p>
                )}
                </form>
                  </div>
                </div>
              </div>
            )}
          </>
        );
      })()}
    </div>
  );
}

function Meta({ label, value, href }: { label: string; value: string; href?: string | null }) {
  return (
    <div className="lg:py-2">
      <dt className="text-[10px] uppercase tracking-wide text-[var(--faint)]">{label}</dt>
      <dd className="mt-0.5 truncate font-medium" title={value}>
        {href ? (
          // Opens the same record in the PSA so a note or a time entry can be checked at source.
          // noreferrer as well as noopener: the PSA has no need to know where the click came from.
          <a
            href={href}
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-1 text-brand underline decoration-brand/40 underline-offset-2 hover:decoration-brand"
          >
            {value}
            <ExternalLink size={12} aria-hidden="true" />
            <span className="sr-only">— open in the PSA (opens in a new tab)</span>
          </a>
        ) : (
          value
        )}
      </dd>
    </div>
  );
}
