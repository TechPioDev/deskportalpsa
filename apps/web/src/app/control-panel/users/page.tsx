'use client';

import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Users, UserPlus, ShieldCheck, User as UserIcon, CheckCircle2, AlertTriangle, Info,
  SlidersHorizontal, Power, X, Send,
} from 'lucide-react';
import { api, type Capabilities, type ClientUser } from '@/lib/api';

// Delegable sections a company administrator can grant to a user (Users management stays admin-only).
const SECTIONS: { key: string; label: string; live: boolean }[] = [
  { key: 'ticketInstructions', label: 'Ticket Instructions', live: true },
  { key: 'approvers', label: 'Approvers', live: true },
  { key: 'escalation', label: 'Escalation Procedures', live: true },
  { key: 'businessHours', label: 'Business Hours', live: true },
  { key: 'holidays', label: 'Holidays', live: true },
  { key: 'accounts', label: 'Accounts & Devices', live: true },
  { key: 'announcements', label: 'Announcements', live: true },
  { key: 'knowledgeBase', label: 'Knowledge Base', live: true },
  { key: 'reports', label: 'Reports', live: true },
  { key: 'branding', label: 'Branding', live: true },
];

export default function UsersPage() {
  const qc = useQueryClient();
  const { data: caps } = useQuery({ queryKey: ['cp-capabilities'], queryFn: api.cpCapabilities, retry: false });
  const { data: users, isLoading, error } = useQuery({ queryKey: ['cp-users'], queryFn: api.cpUsers, retry: false });
  const [managing, setManaging] = useState<string | null>(null);

  const setActive = useMutation({
    mutationFn: ({ id, active }: { id: string; active: boolean }) => api.cpSetUserActive(id, active),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['cp-users'] }),
  });

  return (
    <div className="mx-auto max-w-5xl space-y-6">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-semibold tracking-tight">
          <Users size={22} className="text-brand" /> Users
        </h1>
        <p className="mt-1 text-sm text-[var(--muted)]">
          Invite people from your organization and grant them access to only what they need.
        </p>
      </div>

      {error && (
        <div className="flex items-center gap-2 rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-800 dark:border-amber-900 dark:bg-amber-950/40 dark:text-amber-200">
          <AlertTriangle size={16} /> Only a company administrator can manage users.
        </div>
      )}

      {!error && <InviteCard onDone={() => qc.invalidateQueries({ queryKey: ['cp-users'] })} />}

      <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
        <div className="border-b border-[var(--border)] px-5 py-3.5 text-sm font-semibold">
          {caps?.companyName ? `${caps.companyName} — Users` : 'Users'}
        </div>
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="text-left text-[10px] uppercase tracking-wide text-[var(--faint)]">
              <tr className="border-b border-[var(--border)]">
                <th className="px-5 py-2.5 font-medium">User</th>
                <th className="px-5 py-2.5 font-medium">Role</th>
                <th className="px-5 py-2.5 font-medium">Access</th>
                <th className="px-5 py-2.5 font-medium">Status</th>
                <th className="px-5 py-2.5 text-right font-medium">Manage</th>
              </tr>
            </thead>
            <tbody>
              {isLoading && <tr><td colSpan={5} className="px-5 py-8 text-center text-[var(--muted)]">Loading users…</td></tr>}
              {users?.length === 0 && <tr><td colSpan={5} className="px-5 py-8 text-center text-[var(--muted)]">No users yet — invite your first teammate above.</td></tr>}
              {users?.map((u) => (
                <UserRow
                  key={u.id} user={u} caps={caps}
                  expanded={managing === u.id}
                  onToggleManage={() => setManaging(managing === u.id ? null : u.id)}
                  onSetActive={(active) => setActive.mutate({ id: u.id, active })}
                  busy={setActive.isPending}
                />
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}

function InviteCard({ onDone }: { onDone: () => void }) {
  const [email, setEmail] = useState('');
  const [name, setName] = useState('');
  const [isAdmin, setIsAdmin] = useState(false);

  const invite = useMutation({
    mutationFn: () => api.cpInviteUser({ email: email.trim(), displayName: name.trim(), isCompanyAdministrator: isAdmin }),
    onSuccess: () => { setEmail(''); setName(''); setIsAdmin(false); onDone(); },
  });

  const valid = email.includes('@') && name.trim().length > 0;

  return (
    <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-5">
      <div className="mb-3 flex items-center gap-2 text-sm font-semibold"><UserPlus size={16} className="text-brand" /> Invite a user</div>
      <div className="flex flex-wrap items-end gap-3">
        <label className="flex-1 min-w-[180px] text-xs font-medium text-[var(--muted)]">
          Display name
          <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Jane Doe"
            className="mt-1 w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm text-[var(--fg)] outline-none focus:border-brand" />
        </label>
        <label className="flex-1 min-w-[200px] text-xs font-medium text-[var(--muted)]">
          Email
          <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} placeholder="jane@company.com"
            className="mt-1 w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm text-[var(--fg)] outline-none focus:border-brand" />
        </label>
        <label className="flex items-center gap-2 pb-2 text-sm">
          <input type="checkbox" checked={isAdmin} onChange={(e) => setIsAdmin(e.target.checked)} className="h-4 w-4 accent-brand" />
          Administrator
        </label>
        <button onClick={() => invite.mutate()} disabled={!valid || invite.isPending}
          className="inline-flex items-center gap-2 rounded-lg bg-brand px-4 py-2 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-40">
          <UserPlus size={15} /> {invite.isPending ? 'Inviting…' : 'Invite'}
        </button>
      </div>
      {invite.isError && <p className="mt-2 inline-flex items-center gap-1 text-xs text-red-600 dark:text-red-400"><AlertTriangle size={12} /> {(invite.error as Error)?.message ?? 'Invite failed'}</p>}
            {invite.isSuccess && (
        <div className="mt-2 space-y-1 text-xs">
          <p className="inline-flex items-center gap-1 text-green-600 dark:text-green-400"><CheckCircle2 size={12} />
            {invite.data.invitation.mailed ? `Invitation e-mailed to ${invite.data.user.email}. They choose a password through it and are in.` : `User added. The invitation could not be e-mailed (${invite.data.invitation.mailError ?? 'no mail account'}); send them this link instead:`}
          </p>
          <input readOnly value={invite.data.invitation.link} aria-label="Invitation link" onFocus={(e) => e.currentTarget.select()}
            className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-1 font-mono text-[11px]" />
        </div>
      )}
      <p className="mt-2 flex items-center gap-1.5 text-xs text-[var(--faint)]"><Info size={12} /> Administrators can manage everything for your account. Everyone else gets only the sections you grant.</p>
    </div>
  );
}

function UserRow({ user, caps, expanded, onToggleManage, onSetActive, busy }: {
  user: ClientUser; caps?: Capabilities; expanded: boolean;
  onToggleManage: () => void; onSetActive: (active: boolean) => void; busy: boolean;
}) {
  const reinvite = useMutation({ mutationFn: () => api.cpReinviteUser(user.id) });
  const accessSummary = user.isCompanyAdministrator
    ? 'All sections'
    : user.grants.length === 0 ? 'No access yet'
    : `${user.grants.length} section${user.grants.length === 1 ? '' : 's'}`;

  return (
    <>
      <tr className="border-b border-[var(--border)] last:border-0">
        <td className="px-5 py-3">
          <div className="font-medium">{user.displayName}</div>
          <div className="text-xs text-[var(--muted)]">{user.email}</div>
        </td>
        <td className="px-5 py-3">
          {user.isCompanyAdministrator
            ? <span className="inline-flex items-center gap-1.5 rounded-full bg-violet-100 px-2 py-0.5 text-xs font-medium text-violet-700 dark:bg-violet-950/60 dark:text-violet-300"><ShieldCheck size={12} /> Administrator</span>
            : <span className="inline-flex items-center gap-1.5 rounded-full bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-600 dark:bg-slate-800 dark:text-slate-300"><UserIcon size={12} /> User</span>}
        </td>
        <td className="px-5 py-3 text-[var(--muted)]">{accessSummary}</td>
        <td className="px-5 py-3">
          {!user.isActive
            ? <span className="inline-flex items-center gap-1.5 text-xs font-medium text-[var(--faint)]">Disabled</span>
            : !user.signInLinked
              ? <span className="inline-flex items-center gap-1.5 text-xs font-medium text-amber-700 dark:text-amber-400" title="Has not set a password yet. Resend the invitation if it was lost.">Invited</span>
              : <span className="inline-flex items-center gap-1.5 text-xs font-medium text-green-600 dark:text-green-400"><CheckCircle2 size={13} /> Active</span>}
        </td>
        <td className="px-5 py-3">
          <div className="flex items-center justify-end gap-1">
            {user.isActive && !user.signInLinked && (
              <button onClick={() => reinvite.mutate()} disabled={busy || reinvite.isPending} title="A fresh invitation e-mail; the earlier link stops working."
                className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium text-[var(--fg)] hover:bg-[var(--bg)] disabled:opacity-40">
                <Send size={13} /> {reinvite.isPending ? 'Sending…' : reinvite.isSuccess ? 'Sent' : 'Resend invitation'}
              </button>
            )}
            <button onClick={onToggleManage} className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)]">
              <SlidersHorizontal size={13} /> Access
            </button>
            <button onClick={() => onSetActive(!user.isActive)} disabled={busy} aria-label={user.isActive ? 'Disable user' : 'Enable user'}
              className="rounded-lg p-1.5 text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)] disabled:opacity-40">
              <Power size={15} />
            </button>
          </div>
        </td>
      </tr>
      {expanded && (
        <tr className="border-b border-[var(--border)] bg-[var(--bg)]/40 last:border-0">
          <td colSpan={5} className="px-5 py-4">
            <AccessEditor user={user} caps={caps} onClose={onToggleManage} />
          </td>
        </tr>
      )}
    </>
  );
}

type Scope = 'none' | 'account' | 'all';

function AccessEditor({ user, caps, onClose }: { user: ClientUser; caps?: Capabilities; onClose: () => void }) {
  const qc = useQueryClient();
  const companyId = caps?.clientCompanyId ?? null;

  const initialScope = (key: string): Scope => {
    const g = user.grants.find((x) => x.section === key);
    if (!g) return 'none';
    return g.clientCompanyId ? 'account' : 'all';
  };

  const [isAdmin, setIsAdmin] = useState(user.isCompanyAdministrator);
  const [scopes, setScopes] = useState<Record<string, Scope>>(
    Object.fromEntries(SECTIONS.map((s) => [s.key, initialScope(s.key)])),
  );

  const save = useMutation({
    mutationFn: () => api.cpSetUserAccess(user.id, {
      isCompanyAdministrator: isAdmin,
      grants: Object.entries(scopes)
        .filter(([, v]) => v !== 'none')
        .map(([section, v]) => ({ section, clientCompanyId: v === 'account' ? companyId : null })),
    }),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['cp-users'] }); onClose(); },
  });

  return (
    <div className="rounded-lg border border-[var(--border)] bg-[var(--surface)] p-4">
      <div className="mb-3 flex items-center justify-between">
        <div className="text-sm font-semibold">Access for {user.displayName}</div>
        <button onClick={onClose} className="rounded-md p-1 text-[var(--muted)] hover:bg-[var(--bg)]"><X size={15} /></button>
      </div>

      <label className="flex items-center gap-2 rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm">
        <input type="checkbox" checked={isAdmin} onChange={(e) => setIsAdmin(e.target.checked)} className="h-4 w-4 accent-brand" />
        <ShieldCheck size={15} className="text-violet-500" />
        <span><strong>Company Administrator</strong> — full access to every section (overrides the grants below).</span>
      </label>

      <div className={`mt-3 space-y-1.5 ${isAdmin ? 'pointer-events-none opacity-40' : ''}`}>
        {SECTIONS.map((s) => (
          <div key={s.key} className="flex items-center justify-between gap-3 rounded-lg px-3 py-2 hover:bg-[var(--bg)]">
            <span className="flex items-center gap-2 text-sm">
              {s.label}
              {!s.live && <span className="rounded-full bg-[var(--bg)] px-1.5 py-0.5 text-[9px] font-semibold uppercase tracking-wide text-[var(--faint)]">Soon</span>}
            </span>
            <select
              value={scopes[s.key]}
              disabled={!s.live}
              onChange={(e) => setScopes({ ...scopes, [s.key]: e.target.value as Scope })}
              className="appearance-none rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-1.5 text-sm outline-none focus:border-brand disabled:opacity-50"
            >
              <option value="none">No access</option>
              <option value="account">This account</option>
              <option value="all">All accounts</option>
            </select>
          </div>
        ))}
      </div>

      <div className="mt-4 flex items-center justify-end gap-2">
        {save.isError && <span className="mr-auto inline-flex items-center gap-1 text-xs text-red-600 dark:text-red-400"><AlertTriangle size={12} /> {(save.error as Error)?.message ?? 'Save failed'}</span>}
        <button onClick={onClose} className="rounded-lg border border-[var(--border)] px-3 py-2 text-sm font-medium text-[var(--muted)] hover:bg-[var(--bg)]">Cancel</button>
        <button onClick={() => save.mutate()} disabled={save.isPending}
          className="inline-flex items-center gap-2 rounded-lg bg-brand px-4 py-2 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-40">
          <CheckCircle2 size={15} /> {save.isPending ? 'Saving…' : 'Save access'}
        </button>
      </div>
    </div>
  );
}
