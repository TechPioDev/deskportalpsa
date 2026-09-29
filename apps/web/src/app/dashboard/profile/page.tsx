'use client';

import { useState } from 'react';
import { PushNotificationsCard } from '@/components/PushNotificationsCard';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { BadgeCheck, Building2, CalendarDays, KeyRound, Pencil, ShieldCheck } from 'lucide-react';
import { api } from '@/lib/api';
import type { Profile } from '@/lib/types';

/**
 * The signed-in user's own profile — every role.
 *
 * Three deliberate design decisions, each fixing a flaw in the first mockup:
 *
 * 1. **No Edit button on Role.** A control that lets a user raise their own role
 *    is privilege escalation with good typography. The row says where roles are
 *    actually managed instead.
 * 2. **Name and email edit in place** with save/cancel, and the email row states
 *    the one fact that stops a support ticket later: when sign-in is IdP-bound,
 *    changing the contact email does not change how you log in.
 * 3. **No decorative claims.** The old cards said "Secure" and "Last active:
 *    just now" unconditionally — statements with no data behind them. The cards
 *    now show only facts the API actually returns: roles held, how sign-in is
 *    managed, and company/tenure.
 */

// Role names arrive as stored ("MspAdministrator"); readers get words ("MSP Administrator").
const humanizeRole = (r: string) =>
  r.replace(/^Msp/, 'MSP ').replace(/([a-z])([A-Z])/g, '$1 $2').replace(/ +/g, ' ').trim();

const fmtDate = (iso: string) =>
  new Date(iso).toLocaleDateString(undefined, { day: 'numeric', month: 'long', year: 'numeric' });

function EditableRow({
  icon: Icon,
  label,
  value,
  note,
  onSave,
  inputType = 'text',
}: {
  icon: typeof Pencil;
  label: string;
  value: string;
  note?: string;
  onSave: (next: string) => Promise<void>;
  inputType?: string;
}) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(value);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      await onSave(draft);
      setEditing(false);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not save.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="flex items-start gap-4 py-4">
      <div className="grid size-10 shrink-0 place-items-center rounded-lg bg-[var(--surface-2,#f1f5f9)]">
        <Icon className="size-4 text-[var(--muted)]" aria-hidden />
      </div>
      <div className="min-w-0 flex-1">
        <p className="text-xs text-[var(--muted)]">{label}</p>
        {editing ? (
          <div className="mt-1 space-y-2">
            <input
              type={inputType}
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              aria-label={label}
              className="w-full rounded-lg border border-[var(--border)] bg-transparent px-3 py-1.5 text-sm"
            />
            {error ? <p className="text-xs text-red-600">{error}</p> : null}
            <div className="flex gap-2">
              <button
                type="button"
                onClick={() => void save()}
                disabled={busy}
                className="rounded-lg bg-[var(--accent,#2563eb)] px-3 py-1.5 text-xs font-medium text-white disabled:opacity-50"
              >
                {busy ? 'Saving…' : 'Save'}
              </button>
              <button
                type="button"
                onClick={() => {
                  setDraft(value);
                  setError(null);
                  setEditing(false);
                }}
                className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-xs"
              >
                Cancel
              </button>
            </div>
          </div>
        ) : (
          <>
            <p className="truncate text-sm font-medium">{value}</p>
            {note ? <p className="mt-0.5 text-xs text-[var(--muted)]">{note}</p> : null}
          </>
        )}
      </div>
      {!editing ? (
        <button
          type="button"
          onClick={() => {
            setDraft(value);
            setEditing(true);
          }}
          className="flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-3 py-1.5 text-xs font-medium hover:bg-[var(--surface-2,#f1f5f9)]"
        >
          <Pencil className="size-3.5" aria-hidden /> Edit
        </button>
      ) : null}
    </div>
  );
}

function ReadOnlyRow({
  icon: Icon,
  label,
  value,
  note,
}: {
  icon: typeof Pencil;
  label: string;
  value: string;
  note?: string;
}) {
  return (
    <div className="flex items-start gap-4 py-4">
      <div className="grid size-10 shrink-0 place-items-center rounded-lg bg-[var(--surface-2,#f1f5f9)]">
        <Icon className="size-4 text-[var(--muted)]" aria-hidden />
      </div>
      <div className="min-w-0 flex-1">
        <p className="text-xs text-[var(--muted)]">{label}</p>
        <p className="text-sm font-medium">{value}</p>
        {note ? <p className="mt-0.5 text-xs text-[var(--muted)]">{note}</p> : null}
      </div>
    </div>
  );
}

export default function ProfilePage() {
  const qc = useQueryClient();
  const { data, isError } = useQuery({ queryKey: ['profile'], queryFn: api.profile });

  const update = useMutation({
    mutationFn: api.updateProfile,
    onSuccess: (next: Profile) => qc.setQueryData(['profile'], next),
  });

  const saveField = (field: 'displayName' | 'email') => async (value: string) => {
    if (!data) return;
    await update.mutateAsync({
      displayName: field === 'displayName' ? value : data.displayName,
      email: field === 'email' ? value : data.email,
    });
  };

  if (isError) {
    return (
      <div className="mx-auto max-w-3xl">
        <h1 className="text-xl font-semibold">Profile</h1>
        <p className="mt-3 text-sm text-[var(--muted)]">
          Sign in to load your profile. This preview runs without a live backend.
        </p>
      </div>
    );
  }
  if (!data) return <div className="mx-auto max-w-3xl animate-pulse text-sm text-[var(--muted)]">Loading…</div>;

  const initial = (data.displayName || '?').slice(0, 1).toUpperCase();
  const roleLine = data.roles.map(humanizeRole).join(' · ') || (data.kind === 'staff' ? 'Staff' : 'Client user');

  return (
    <div className="mx-auto max-w-3xl space-y-6">
      <div>
        <h1 className="text-xl font-semibold">Profile</h1>
        <p className="text-sm text-[var(--muted)]">Manage your account details and preferences.</p>
      </div>

      {/* Staff only: push notifications are for the people who work tickets. */}
      {data.kind === 'staff' && <PushNotificationsCard />}

      <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-6">
        <div className="flex flex-col gap-6 sm:flex-row">
          <div className="flex flex-col items-center gap-3 sm:w-52 sm:border-r sm:border-[var(--border)] sm:pr-6">
            <div className="grid size-24 place-items-center rounded-full bg-[var(--accent,#2563eb)]/10 text-3xl font-bold text-[var(--accent,#2563eb)]">
              {initial}
            </div>
            <p className="text-lg font-semibold">{data.displayName}</p>
            <span className="rounded-full bg-[var(--accent,#2563eb)]/10 px-3 py-1 text-xs font-medium text-[var(--accent,#2563eb)]">
              {roleLine}
            </span>
            {data.companyName ? <p className="text-xs text-[var(--muted)]">{data.companyName}</p> : null}
          </div>

          <div className="min-w-0 flex-1 divide-y divide-[var(--border)]">
            <EditableRow
              icon={BadgeCheck}
              label="Full name"
              value={data.displayName}
              onSave={saveField('displayName')}
            />
            <EditableRow
              icon={KeyRound}
              label="Email address"
              value={data.email}
              inputType="email"
              note={
                data.signInManaged
                  ? 'Contact address. Sign-in is managed by your identity provider, so changing this does not change how you log in.'
                  : undefined
              }
              onSave={saveField('email')}
            />
            <ReadOnlyRow
              icon={ShieldCheck}
              label={data.roles.length > 1 ? 'Roles' : 'Role'}
              value={roleLine}
              note={
                data.kind === 'staff'
                  ? 'Roles are assigned by your administrator in user management.'
                  : 'Your access level is managed by your company administrator.'
              }
            />
            <ReadOnlyRow icon={CalendarDays} label="Member since" value={fmtDate(data.memberSince)} />
          </div>
        </div>
      </div>

      <div className="grid gap-4 sm:grid-cols-3">
        <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-5">
          <div className="mb-2 grid size-10 place-items-center rounded-lg bg-blue-50">
            <ShieldCheck className="size-5 text-blue-600" aria-hidden />
          </div>
          <p className="text-sm font-semibold">Access</p>
          <p className="mt-1 text-sm text-[var(--muted)]">
            {data.kind === 'staff'
              ? `${data.roles.length || 'No'} role${data.roles.length === 1 ? '' : 's'} assigned: ${roleLine}.`
              : data.isCompanyAdministrator
                ? 'Company administrator: you manage users and settings for your company.'
                : 'Client user: you can raise and follow your tickets.'}
          </p>
        </div>
        <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-5">
          <div className="mb-2 grid size-10 place-items-center rounded-lg bg-emerald-50">
            <KeyRound className="size-5 text-emerald-600" aria-hidden />
          </div>
          <p className="text-sm font-semibold">Sign-in</p>
          <p className="mt-1 text-sm text-[var(--muted)]">
            {data.signInManaged
              ? 'Single sign-on via your identity provider. Password and MFA are managed there.'
              : 'Local development sign-in. Production accounts use single sign-on.'}
          </p>
        </div>
        <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-5">
          <div className="mb-2 grid size-10 place-items-center rounded-lg bg-violet-50">
            <Building2 className="size-5 text-violet-600" aria-hidden />
          </div>
          <p className="text-sm font-semibold">{data.kind === 'client' ? 'Company' : 'Organization'}</p>
          <p className="mt-1 text-sm text-[var(--muted)]">
            {data.companyName ?? 'MSP staff account'} · member since {fmtDate(data.memberSince)}.
          </p>
        </div>
      </div>
    </div>
  );
}
