'use client';

import { useMemo, useState, type ReactNode } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ImportStaffDrawer } from '@/components/ImportStaffDrawer';
import {
  UserPlus, Search, X, MailQuestion, Power, MoreVertical,
  Users, UserCheck, Crown, Trash2, Copy, Pencil,
  FileSpreadsheet,
} from 'lucide-react';
import {
  api, BoardAccessMode as BAM,
  type UserSummary, type RoleOption, type DepartmentWithTeams, type BoardOption,
  type PermissionTemplateOption, type UserListParams, type BulkUserActionName,
} from '@/lib/api';

/**
 * The MSP's own people: technicians, managers, administrators, auditors — plus the roles,
 * departments, teams, board access and permission templates layered on top of them.
 *
 * Creating a user here is an INVITATION, not a credential: sign-in stays with the identity
 * provider, and the account binds to it the first time the person logs in with a token whose
 * verified email matches. "Last active" is the one real signal this app has (a throttled stamp
 * on authenticated requests) — there is no login-event log to draw a true "last login" from, so
 * it is labeled for what it actually is rather than dressed up as something else.
 */
const ALL = '__all__';

const humanizeRole = (r: string) =>
  r.replace(/^Msp/, 'MSP ').replace(/([a-z])([A-Z])/g, '$1 $2').replace(/ +/g, ' ').trim();

const boardModeLabel = (mode: number) =>
  mode === BAM.Selected ? 'Selected boards' : mode === BAM.None ? 'No board access' : 'All boards';

function relativeTime(iso: string | null): string {
  if (!iso) return '—';
  const diffMin = Math.round((Date.now() - new Date(iso).getTime()) / 60_000);
  if (diffMin < 1) return 'just now';
  if (diffMin < 60) return `${diffMin}m ago`;
  const diffHr = Math.round(diffMin / 60);
  if (diffHr < 24) return `${diffHr}h ago`;
  const diffDay = Math.round(diffHr / 24);
  if (diffDay < 30) return `${diffDay}d ago`;
  return new Date(iso).toLocaleDateString();
}

function FilterSelect({ label, value, onChange, options }: {
  label: string; value: string; onChange: (v: string) => void; options: { value: string; label: string }[];
}) {
  return (
    <label className="flex items-center gap-1.5 text-xs">
      <span className="text-[var(--muted)]">{label}</span>
      <select value={value} onChange={(e) => onChange(e.target.value)}
        className="rounded-lg border border-[var(--border)] bg-[var(--surface)] px-2 py-1.5 text-sm outline-none focus:border-brand">
        <option value={ALL}>All</option>
        {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
    </label>
  );
}

function SummaryCard({ icon: Icon, label, value }: { icon: typeof Users; label: string; value: number }) {
  return (
    <div className="flex items-center gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] px-4 py-3">
      <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-brand/10 text-brand">
        <Icon size={16} />
      </span>
      <span>
        <span className="block text-lg font-semibold leading-tight">{value}</span>
        <span className="block text-xs text-[var(--muted)]">{label}</span>
      </span>
    </div>
  );
}

function DrawerShell({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  return (
    <div className="fixed inset-0 z-50 flex justify-end">
      <div className="absolute inset-0 bg-black/30" onClick={onClose} />
      <div className="relative flex h-full w-full max-w-md flex-col overflow-y-auto border-l border-[var(--border)] bg-[var(--surface)] shadow-xl">
        <div className="flex items-center justify-between border-b border-[var(--border)] px-5 py-4">
          <h2 className="text-sm font-semibold">{title}</h2>
          <button onClick={onClose} aria-label="Close" className="text-[var(--muted)] hover:text-[var(--fg)]"><X size={18} /></button>
        </div>
        <div className="flex-1 space-y-6 px-5 py-4">{children}</div>
      </div>
    </div>
  );
}

function RowMenu({ open, onToggle, onClose, isActive, onManage, onToggleActive, onCopy, onDelete }: {
  open: boolean; onToggle: () => void; onClose: () => void; isActive: boolean;
  onManage: () => void; onToggleActive: () => void; onCopy: () => void; onDelete: () => void;
}) {
  return (
    <div className="relative inline-block text-left"
      onBlur={(e) => { if (!e.currentTarget.contains(e.relatedTarget as Node)) onClose(); }}>
      <button onClick={onToggle} aria-label="Actions" className="rounded-lg p-1.5 text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)]">
        <MoreVertical size={16} />
      </button>
      {open && (
        <div className="absolute right-0 z-10 mt-1 w-56 rounded-lg border border-[var(--border)] bg-[var(--surface)] py-1 text-sm shadow-lg">
          <button onClick={onManage} className="flex w-full items-center gap-2 px-3 py-1.5 text-left hover:bg-[var(--bg)]">
            <Pencil size={13} /> Manage
          </button>
          <button onClick={onToggleActive} className="flex w-full items-center gap-2 px-3 py-1.5 text-left hover:bg-[var(--bg)]">
            <Power size={13} /> {isActive ? 'Deactivate' : 'Reactivate'}
          </button>
          <button onClick={onCopy} className="flex w-full items-center gap-2 px-3 py-1.5 text-left hover:bg-[var(--bg)]">
            <Copy size={13} /> Copy sign-in instructions
          </button>
          <div className="my-1 border-t border-[var(--border)]" />
          <button onClick={onDelete} className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-red-600 hover:bg-red-50 dark:hover:bg-red-950/40">
            <Trash2 size={13} /> Delete
          </button>
        </div>
      )}
    </div>
  );
}

const BOARD_MODES: { value: number; label: string }[] = [
  { value: BAM.All, label: 'All boards' },
  { value: BAM.Selected, label: 'Selected' },
  { value: BAM.None, label: 'None' },
];

/**
 * Bringing PSA technicians into the portal.
 *
 * Deliberately one confirmed decision at a time. A PSA's resource list carries API users, service
 * accounts and people who have left, and anything created here becomes a real login — so this
 * screen shows suggestions and their current state, and an administrator picks.
 */
function ImportFromPsaDrawer({ open, onClose, onChanged }: { open: boolean; onClose: () => void; onChanged: () => void }) {
  const [connectionId, setConnectionId] = useState('');
  const { data: connections } = useQuery({
    queryKey: ['connections'], queryFn: api.connections, enabled: open, retry: false,
  });
  const chosen = connectionId || connections?.[0]?.id || '';
  const { data: techs, isLoading, error, refetch } = useQuery({
    queryKey: ['psa-technicians', chosen],
    queryFn: () => api.psaTechnicians(chosen),
    enabled: open && !!chosen,
    retry: false,
  });
  const provision = useMutation({
    mutationFn: (externalId: string) => api.provisionTechnician(chosen, externalId),
    onSuccess: () => { refetch(); onChanged(); },
  });

  if (!open) return null;

  return (
    <DrawerShell title="Import from PSA" onClose={onClose}>
      <p className="text-sm text-[var(--muted)]">
        Technicians as your PSA lists them. Adding someone creates their portal account and maps it
        to their PSA identity, so time they log is attributed to them rather than the connection default.
      </p>
      <p className="rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-xs leading-relaxed text-[var(--muted)]">
        They will also need a sign-in account in your identity provider. The portal binds the two by
        verified email the first time they log in.
      </p>

      {(connections?.length ?? 0) > 1 && (
        <label className="block text-sm">
          <span className="mb-1 block font-medium">Connection</span>
          <select value={chosen} onChange={(e) => setConnectionId(e.target.value)}
            className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand">
            {connections!.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        </label>
      )}

      {isLoading && <p className="text-sm text-[var(--muted)]">Reading technicians from the PSA…</p>}
      {error && <p className="text-sm text-red-600 dark:text-red-400">{(error as Error).message}</p>}
      {provision.isError && (
        <p className="text-sm text-red-600 dark:text-red-400">{(provision.error as Error).message}</p>
      )}

      <ul className="space-y-2">
        {techs?.map((t) => (
          <li key={t.externalId}
            className="flex flex-wrap items-center gap-2 rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2">
            <div className="min-w-0 flex-1">
              <p className="truncate text-sm font-medium">{t.name || t.email || t.externalId}</p>
              <p className="truncate text-xs text-[var(--muted)]">{t.email || 'No email in the PSA'}</p>
              {t.blocker && <p className="mt-0.5 text-xs text-amber-700 dark:text-amber-300">{t.blocker}</p>}
            </div>
            {!t.isActive && (
              <span className="rounded bg-slate-200/70 px-1.5 py-0.5 text-[11px] text-slate-600 dark:bg-slate-800 dark:text-slate-300">
                Inactive in PSA
              </span>
            )}
            {t.link === 2 ? (
              <span className="rounded-full bg-brand-tint px-2 py-0.5 text-[11px] font-medium text-brand dark:bg-brand/20">
                In portal
              </span>
            ) : (
              <button type="button"
                disabled={!t.canProvision || provision.isPending}
                title={t.blocker ?? undefined}
                onClick={() => provision.mutate(t.externalId)}
                className="rounded-lg bg-brand px-2.5 py-1 text-xs font-medium text-brand-fg hover:opacity-90 disabled:opacity-40">
                {t.link === 1 ? 'Link' : 'Add'}
              </button>
            )}
          </li>
        ))}
      </ul>
      {techs?.length === 0 && <p className="text-sm text-[var(--muted)]">This PSA returned no technicians.</p>}
    </DrawerShell>
  );
}

function AddUserDrawer({ roles, departments, boards, templates, onClose, onCreated }: {
  roles: RoleOption[]; departments: DepartmentWithTeams[]; boards: BoardOption[];
  templates: PermissionTemplateOption[]; onClose: () => void; onCreated: () => void;
}) {
  const [name, setName] = useState('');
  const [email, setEmail] = useState('');
  const [phone, setPhone] = useState('');
  const [location, setLocation] = useState('');
  const [pickedRoles, setPickedRoles] = useState<Set<string>>(new Set());
  const [templateId, setTemplateId] = useState('');
  const [departmentId, setDepartmentId] = useState('');
  const [teamId, setTeamId] = useState('');
  const [boardMode, setBoardMode] = useState<number>(BAM.All);
  const [pickedBoards, setPickedBoards] = useState<Set<string>>(new Set());

  const teamsForDept = departments.find((d) => d.id === departmentId)?.teams ?? [];

  const toggleRole = (id: string) => setPickedRoles((p) => {
    const next = new Set(p); if (next.has(id)) next.delete(id); else next.add(id); return next;
  });
  const toggleBoard = (key: string) => setPickedBoards((p) => {
    const next = new Set(p); if (next.has(key)) next.delete(key); else next.add(key); return next;
  });

  const submit = useMutation({
    mutationFn: async () => {
      const created = await api.createStaffUser({ displayName: name.trim(), email: email.trim(), roleIds: [...pickedRoles] });
      if (phone.trim() || location.trim()) {
        await api.updateStaffUser(created.id, {
          displayName: created.displayName, email: created.email,
          phoneNumber: phone.trim() || null, location: location.trim() || null,
        });
      }
      if (departmentId) await api.setUserDepartment(created.id, departmentId, true);
      if (teamId) await api.assignUserTeam(created.id, teamId);
      if (boardMode !== BAM.All) {
        await api.setUserBoardAccessMode(created.id, boardMode);
        if (boardMode === BAM.Selected) {
          for (const key of pickedBoards) {
            const [psaConnectionId, boardName] = key.split('::');
            await api.setUserBoardGrant(created.id, { psaConnectionId, boardName, actions: 1 });
          }
        }
      }
      if (templateId) await api.applyPermissionTemplate(created.id, templateId);
      return created;
    },
    onSuccess: onCreated,
  });

  const canSubmit = name.trim().length >= 2 && email.trim() !== '' && pickedRoles.size > 0 && !submit.isPending;

  return (
    <DrawerShell title="Add user" onClose={onClose}>
      <form onSubmit={(e) => { e.preventDefault(); submit.mutate(); }} className="space-y-6">
        <section className="space-y-3">
          <h3 className="text-xs font-semibold uppercase tracking-wide text-[var(--faint)]">Basic info</h3>
          <label className="block">
            <span className="mb-1 block text-xs font-medium">Full name *</span>
            <input value={name} onChange={(e) => setName(e.target.value)} required minLength={2}
              className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand" />
          </label>
          <label className="block">
            <span className="mb-1 block text-xs font-medium">Email *</span>
            <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required
              className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand" />
            <span className="mt-1 block text-xs text-[var(--muted)]">Must match their identity-provider email — that&apos;s how sign-in binds.</span>
          </label>
          <label className="block">
            <span className="mb-1 block text-xs font-medium">Phone</span>
            <input value={phone} onChange={(e) => setPhone(e.target.value)}
              className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand" />
          </label>
          <label className="block">
            <span className="mb-1 block text-xs font-medium">Location</span>
            <input value={location} onChange={(e) => setLocation(e.target.value)}
              className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand" />
          </label>
        </section>

        <section className="space-y-3">
          <h3 className="text-xs font-semibold uppercase tracking-wide text-[var(--faint)]">Role & template</h3>
          <div>
            <span className="mb-1 block text-xs font-medium">Roles *</span>
            <div className="flex flex-wrap gap-1.5">
              {roles.map((r) => (
                <button key={r.id} type="button" onClick={() => toggleRole(r.id)}
                  className={`rounded-full border px-2.5 py-1 text-xs font-medium ${pickedRoles.has(r.id)
                    ? 'border-brand bg-brand/10 text-brand' : 'border-[var(--border)] text-[var(--muted)] hover:bg-[var(--bg)]'}`}>
                  {humanizeRole(r.name)}
                </button>
              ))}
            </div>
          </div>
          <label className="block">
            <span className="mb-1 block text-xs font-medium">Permission template</span>
            <select value={templateId} onChange={(e) => setTemplateId(e.target.value)}
              className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand">
              <option value="">None — use the role&apos;s own defaults</option>
              {templates.map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
            </select>
          </label>
        </section>

        <section className="space-y-3">
          <h3 className="text-xs font-semibold uppercase tracking-wide text-[var(--faint)]">Department & team</h3>
          <label className="block">
            <span className="mb-1 block text-xs font-medium">Department</span>
            <select value={departmentId} onChange={(e) => { setDepartmentId(e.target.value); setTeamId(''); }}
              className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand">
              <option value="">Not set</option>
              {departments.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
            </select>
          </label>
          {teamsForDept.length > 0 && (
            <label className="block">
              <span className="mb-1 block text-xs font-medium">Team</span>
              <select value={teamId} onChange={(e) => setTeamId(e.target.value)}
                className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand">
                <option value="">Not set</option>
                {teamsForDept.map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
              </select>
            </label>
          )}
        </section>

        <section className="space-y-3">
          <h3 className="text-xs font-semibold uppercase tracking-wide text-[var(--faint)]">Board access</h3>
          <div className="flex gap-1.5">
            {BOARD_MODES.map((m) => (
              <button key={m.value} type="button" onClick={() => setBoardMode(m.value)}
                className={`rounded-full border px-2.5 py-1 text-xs font-medium ${boardMode === m.value
                  ? 'border-brand bg-brand/10 text-brand' : 'border-[var(--border)] text-[var(--muted)] hover:bg-[var(--bg)]'}`}>
                {m.label}
              </button>
            ))}
          </div>
          {boardMode === BAM.Selected && (
            <div className="max-h-40 space-y-1 overflow-y-auto rounded-lg border border-[var(--border)] p-2">
              {boards.length === 0 && <p className="text-xs text-[var(--muted)]">No boards found — sync a PSA connection first.</p>}
              {boards.map((b) => {
                const key = `${b.psaConnectionId}::${b.boardName}`;
                return (
                  <label key={key} className="flex items-center gap-2 text-xs">
                    <input type="checkbox" checked={pickedBoards.has(key)} onChange={() => toggleBoard(key)} />
                    {b.boardName} <span className="text-[var(--faint)]">({b.connectionName})</span>
                  </label>
                );
              })}
            </div>
          )}
        </section>

        <div className="flex items-center gap-3 border-t border-[var(--border)] pt-4">
          <button type="submit" disabled={!canSubmit}
            className="inline-flex items-center gap-2 rounded-lg bg-brand px-4 py-2 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
            <UserPlus size={15} /> {submit.isPending ? 'Adding…' : 'Add user'}
          </button>
          {submit.isError && <span className="text-xs text-red-600 dark:text-red-400">
            {submit.error instanceof Error ? submit.error.message : 'Could not add the user.'}</span>}
        </div>
      </form>
    </DrawerShell>
  );
}

export default function UsersPage() {
  const qc = useQueryClient();
  const router = useRouter();

  const [search, setSearch] = useState('');
  const [roleId, setRoleId] = useState(ALL);
  const [departmentId, setDepartmentId] = useState(ALL);
  const [teamId, setTeamId] = useState(ALL);
  const [boardName, setBoardName] = useState(ALL);
  const [status, setStatus] = useState(ALL);
  const [showImport, setShowImport] = useState(false);
  const [showFileImport, setShowFileImport] = useState(false);
  const [page, setPage] = useState(1);
  const pageSize = 25;

  const params: UserListParams = useMemo(() => ({
    search: search.trim() || undefined,
    roleId: roleId === ALL ? undefined : roleId,
    departmentId: departmentId === ALL ? undefined : departmentId,
    teamId: teamId === ALL ? undefined : teamId,
    boardName: boardName === ALL ? undefined : boardName,
    isActive: status === ALL ? undefined : status === 'active',
    page, pageSize,
  }), [search, roleId, departmentId, teamId, boardName, status, page]);

  const { data, isLoading, isError } = useQuery({ queryKey: ['staff-users', params], queryFn: () => api.staffUsers(params) });
  const { data: roles } = useQuery({ queryKey: ['staff-roles'], queryFn: api.staffRoles, staleTime: 5 * 60_000 });
  const { data: departments } = useQuery({ queryKey: ['staff-departments'], queryFn: api.staffDepartments, staleTime: 5 * 60_000 });
  const { data: boards } = useQuery({ queryKey: ['staff-boards'], queryFn: api.staffBoards, staleTime: 60_000 });
  const { data: templates } = useQuery({ queryKey: ['permission-templates'], queryFn: api.permissionTemplates, staleTime: 5 * 60_000 });

  const refresh = () => qc.invalidateQueries({ queryKey: ['staff-users'] });

  const [showAdd, setShowAdd] = useState(false);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [openMenuId, setOpenMenuId] = useState<string | null>(null);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const [bulkError, setBulkError] = useState<string | null>(null);

  const users = useMemo(() => data?.users ?? [], [data]);
  const allTeams = useMemo(() => (departments ?? []).flatMap((d) => d.teams), [departments]);
  const boardNames = useMemo(() => Array.from(new Set((boards ?? []).map((b) => b.boardName))).sort(), [boards]);

  const active = search.trim() !== '' || [roleId, departmentId, teamId, boardName, status].some((v) => v !== ALL);
  const clearFilters = () => {
    setSearch(''); setRoleId(ALL); setDepartmentId(ALL); setTeamId(ALL); setBoardName(ALL); setStatus(ALL); setPage(1);
  };

  const setActive = useMutation({ mutationFn: (v: { id: string; active: boolean }) => api.setUserActive(v.id, v.active), onSuccess: refresh });
  const del = useMutation({
    mutationFn: (id: string) => api.deleteStaffUser(id),
    onSuccess: () => { setDeleteError(null); refresh(); },
    onError: (e) => setDeleteError(e instanceof Error ? e.message : 'Could not delete this user.'),
  });

  const toggleSelected = (id: string) => setSelected((p) => {
    const next = new Set(p); if (next.has(id)) next.delete(id); else next.add(id); return next;
  });
  const toggleSelectAll = () => setSelected((p) => (p.size === users.length ? new Set() : new Set(users.map((u) => u.id))));

  const bulk = useMutation({
    mutationFn: (input: { action: BulkUserActionName; roleId?: string; departmentId?: string; teamId?: string }) =>
      api.bulkUsers({ ...input, userIds: [...selected] }),
    onSuccess: (result) => {
      refresh();
      setSelected(new Set());
      const failed = result.rows.filter((r) => !r.success);
      setBulkError(failed.length > 0 ? `${failed.length} of ${result.rows.length} could not be changed: ${failed.map((f) => f.reason).join('; ')}` : null);
    },
  });

  const copyInstructions = (u: UserSummary) => {
    const text = `Sign in to the portal at ${window.location.origin} using your work account: ${u.email}`;
    navigator.clipboard.writeText(text);
  };

  const totalPages = data ? Math.max(1, Math.ceil(data.totalMatching / data.pageSize)) : 1;

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-xl font-semibold">Users & Access Management</h1>
          <p className="text-sm text-[var(--muted)]">
            Your team&apos;s portal accounts, roles, and access. Sign-in itself stays with your identity provider.
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <button onClick={() => setShowImport(true)}
            className="inline-flex items-center gap-2 rounded-lg border border-[var(--border)] px-3.5 py-2 text-sm font-medium hover:bg-[var(--bg)]">
            <Users size={16} /> Import from PSA
          </button>
          <button onClick={() => setShowFileImport(true)}
            className="inline-flex items-center gap-2 rounded-lg border border-[var(--border)] px-3.5 py-2 text-sm font-medium hover:bg-[var(--bg)]">
            <FileSpreadsheet size={16} /> Import from file
          </button>
          <button onClick={() => setShowAdd(true)}
            className="inline-flex items-center gap-2 rounded-lg bg-brand px-3.5 py-2 text-sm font-medium text-brand-fg hover:opacity-90">
            <UserPlus size={16} /> Add user
          </button>
        </div>
      </div>

      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        <SummaryCard icon={Users} label="Total" value={data?.summary.total ?? 0} />
        <SummaryCard icon={UserCheck} label="Active" value={data?.summary.active ?? 0} />
        <SummaryCard icon={MailQuestion} label="Pending" value={data?.summary.pending ?? 0} />
        <SummaryCard icon={Crown} label="Administrators" value={data?.summary.administrators ?? 0} />
      </div>

      <div className="flex flex-wrap items-center gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] px-4 py-3">
        <div className="relative">
          <Search size={14} className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-[var(--faint)]" />
          <input value={search} onChange={(e) => { setSearch(e.target.value); setPage(1); }} placeholder="Search name or email…"
            className="w-56 rounded-lg border border-[var(--border)] bg-[var(--bg)] py-1.5 pl-8 pr-3 text-sm outline-none focus:border-brand" />
        </div>
        <FilterSelect label="Role" value={roleId} onChange={(v) => { setRoleId(v); setPage(1); }}
          options={(roles ?? []).map((r) => ({ value: r.id, label: humanizeRole(r.name) }))} />
        <FilterSelect label="Department" value={departmentId} onChange={(v) => { setDepartmentId(v); setPage(1); }}
          options={(departments ?? []).map((d) => ({ value: d.id, label: d.name }))} />
        <FilterSelect label="Team" value={teamId} onChange={(v) => { setTeamId(v); setPage(1); }}
          options={allTeams.map((t) => ({ value: t.id, label: t.name }))} />
        <FilterSelect label="Board" value={boardName} onChange={(v) => { setBoardName(v); setPage(1); }}
          options={boardNames.map((b) => ({ value: b, label: b }))} />
        <FilterSelect label="Status" value={status} onChange={(v) => { setStatus(v); setPage(1); }}
          options={[{ value: 'active', label: 'Active' }, { value: 'inactive', label: 'Inactive' }]} />
        <span className="ml-auto text-xs text-[var(--muted)]">
          {data ? `${data.totalMatching} user${data.totalMatching === 1 ? '' : 's'}` : ''}
        </span>
        {active && (
          <button onClick={clearFilters} className="inline-flex items-center gap-1 rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)]">
            <X size={13} /> Clear
          </button>
        )}
      </div>

      {selected.size > 0 && (
        <div className="flex flex-wrap items-center gap-2 rounded-xl border border-brand/30 bg-brand/5 px-4 py-2.5">
          <span className="text-xs font-medium text-brand">{selected.size} selected</span>
          <button onClick={() => bulk.mutate({ action: 'Activate' })} disabled={bulk.isPending}
            className="rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium hover:bg-[var(--bg)] disabled:opacity-50">Activate</button>
          <button onClick={() => bulk.mutate({ action: 'Deactivate' })} disabled={bulk.isPending}
            className="rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium hover:bg-[var(--bg)] disabled:opacity-50">Deactivate</button>
          <select value="" onChange={(e) => { if (e.target.value) bulk.mutate({ action: 'AssignRole', roleId: e.target.value }); }}
            aria-label="Bulk assign role" className="rounded-lg border border-[var(--border)] bg-[var(--surface)] px-2 py-1.5 text-xs">
            <option value="">Assign role…</option>
            {(roles ?? []).map((r) => <option key={r.id} value={r.id}>{humanizeRole(r.name)}</option>)}
          </select>
          <select value="" onChange={(e) => { if (e.target.value) bulk.mutate({ action: 'AssignDepartment', departmentId: e.target.value }); }}
            aria-label="Bulk assign department" className="rounded-lg border border-[var(--border)] bg-[var(--surface)] px-2 py-1.5 text-xs">
            <option value="">Assign department…</option>
            {(departments ?? []).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
          </select>
          <button
            onClick={() => { if (window.confirm(`Delete ${selected.size} user(s)? This cannot be undone.`)) bulk.mutate({ action: 'Delete' }); }}
            disabled={bulk.isPending}
            className="rounded-lg border border-red-300 px-2.5 py-1.5 text-xs font-medium text-red-600 hover:bg-red-50 disabled:opacity-50 dark:hover:bg-red-950/40">
            Delete
          </button>
          {bulkError && <span className="text-xs text-red-600 dark:text-red-400">{bulkError}</span>}
        </div>
      )}

      {deleteError && (
        <p className="text-xs text-red-600 dark:text-red-400">{deleteError}</p>
      )}

      {isLoading && <p className="px-1 text-sm text-[var(--muted)]">Loading…</p>}

      {isError && (
        <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] px-5 py-8 text-center">
          <p className="text-sm font-medium">Could not load users.</p>
          <p className="mt-1 text-xs text-[var(--muted)]">Try refreshing the page.</p>
        </div>
      )}

      {!isLoading && !isError && users.length === 0 && (
        <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] px-5 py-8 text-center">
          <p className="text-sm font-medium">{active ? 'No users match your filters' : 'No staff accounts yet'}</p>
          <p className="mt-1 text-xs text-[var(--muted)]">
            {active ? 'Try a different search term, or clear the filters to see everyone.' : 'Add your first technician to get started.'}
          </p>
        </div>
      )}

      {!isLoading && !isError && users.length > 0 && (
        <div className="overflow-x-auto rounded-xl border border-[var(--border)] bg-[var(--surface)]">
          <table className="w-full text-sm">
            <thead className="text-left text-xs uppercase tracking-wide text-[var(--muted)]">
              <tr className="border-b border-[var(--border)]">
                <th className="w-8 px-4 py-3">
                  <input type="checkbox" aria-label="Select all" checked={users.length > 0 && selected.size === users.length} onChange={toggleSelectAll} />
                </th>
                <th className="px-4 py-3 font-medium">User</th>
                <th className="px-4 py-3 font-medium">Role</th>
                <th className="px-4 py-3 font-medium">Department</th>
                <th className="px-4 py-3 font-medium">Team</th>
                <th className="px-4 py-3 font-medium">Boards</th>
                <th className="px-4 py-3 font-medium">Status</th>
                <th className="px-4 py-3 font-medium">Last active</th>
                <th className="w-10 px-4 py-3" />
              </tr>
            </thead>
            <tbody>
              {users.map((u) => (
                <tr key={u.id} className={`border-b border-[var(--border)] last:border-0 hover:bg-[var(--bg)] ${u.isActive ? '' : 'opacity-60'}`}>
                  <td className="px-4 py-3">
                    <input type="checkbox" aria-label={`Select ${u.displayName}`} checked={selected.has(u.id)} onChange={() => toggleSelected(u.id)} />
                  </td>
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-2.5">
                      {u.photoUrl ? (
                        // eslint-disable-next-line @next/next/no-img-element
                        <img src={u.photoUrl} alt="" className="h-7 w-7 shrink-0 rounded-full object-cover" />
                      ) : (
                        <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-brand/10 text-xs font-semibold text-brand">
                          {u.displayName.slice(0, 1).toUpperCase()}
                        </span>
                      )}
                      <span className="min-w-0">
                        <Link href={`/dashboard/users/${u.id}`} className="block truncate text-sm font-medium hover:underline">{u.displayName}</Link>
                        <span className="block truncate text-xs text-[var(--muted)]">{u.email}</span>
                      </span>
                    </div>
                  </td>
                  <td className="px-4 py-3">
                    <div className="flex flex-wrap gap-1">
                      {u.roles.map((r) => (
                        <span key={r.id} className="inline-flex items-center rounded-full border border-[var(--border)] bg-[var(--bg)] px-2 py-0.5 text-[11px] font-medium">
                          {humanizeRole(r.name)}
                        </span>
                      ))}
                      {u.roles.length === 0 && <span className="text-xs text-[var(--faint)]">—</span>}
                    </div>
                  </td>
                  <td className="px-4 py-3 text-xs">{u.primaryDepartment?.name ?? '—'}</td>
                  <td className="px-4 py-3 text-xs">{u.teams.length > 0 ? u.teams.map((t) => t.name).join(', ') : '—'}</td>
                  <td className="px-4 py-3 text-xs">
                    {boardModeLabel(u.boardAccessMode)}{u.boardAccessMode === BAM.Selected ? ` (${u.boardGrants.length})` : ''}
                  </td>
                  <td className="px-4 py-3">
                    {!u.signInLinked ? (
                      <span title="Created here, but they have not signed in yet. Their account links on first login by email."
                        className="inline-flex items-center gap-1 rounded bg-amber-100 px-1.5 py-0.5 text-[11px] font-medium text-amber-700 dark:bg-amber-950 dark:text-amber-300">
                        <MailQuestion size={11} /> Pending
                      </span>
                    ) : u.isActive ? (
                      <span className="inline-flex items-center gap-1 rounded bg-green-100 px-1.5 py-0.5 text-[11px] font-medium text-green-700 dark:bg-green-950 dark:text-green-300">
                        Active
                      </span>
                    ) : (
                      <span className="inline-flex items-center gap-1 rounded bg-slate-200 px-1.5 py-0.5 text-[11px] font-medium text-slate-600 dark:bg-slate-800 dark:text-slate-300">
                        Deactivated
                      </span>
                    )}
                  </td>
                  <td className="px-4 py-3 text-xs text-[var(--muted)]" title="Last authenticated request seen from this account — not a login-event log.">
                    {relativeTime(u.lastActiveAt)}
                  </td>
                  <td className="px-4 py-3 text-right">
                    <RowMenu
                      open={openMenuId === u.id}
                      onToggle={() => setOpenMenuId(openMenuId === u.id ? null : u.id)}
                      onClose={() => setOpenMenuId((cur) => (cur === u.id ? null : cur))}
                      isActive={u.isActive}
                      onManage={() => { setOpenMenuId(null); router.push(`/dashboard/users/${u.id}`); }}
                      onToggleActive={() => { setActive.mutate({ id: u.id, active: !u.isActive }); setOpenMenuId(null); }}
                      onCopy={() => { copyInstructions(u); setOpenMenuId(null); }}
                      onDelete={() => {
                        setOpenMenuId(null);
                        if (window.confirm(`Delete ${u.displayName}? This removes their account and all role, department, and board assignments.`)) del.mutate(u.id);
                      }}
                    />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {data && data.totalMatching > 0 && (
        <div className="flex items-center justify-between text-xs text-[var(--muted)]">
          <span>{(page - 1) * pageSize + 1}–{Math.min(page * pageSize, data.totalMatching)} of {data.totalMatching}</span>
          <div className="flex items-center gap-2">
            <button disabled={page <= 1} onClick={() => setPage((p) => p - 1)}
              className="rounded-lg border border-[var(--border)] px-2.5 py-1 font-medium disabled:opacity-40">Prev</button>
            <span>Page {page} of {totalPages}</span>
            <button disabled={page >= totalPages} onClick={() => setPage((p) => p + 1)}
              className="rounded-lg border border-[var(--border)] px-2.5 py-1 font-medium disabled:opacity-40">Next</button>
          </div>
        </div>
      )}

      {showFileImport && (
        <DrawerShell title="Import staff from a file" onClose={() => setShowFileImport(false)}>
          <ImportStaffDrawer
            onClose={() => setShowFileImport(false)}
            onImported={() => qc.invalidateQueries({ queryKey: ['staff-users'] })}
          />
        </DrawerShell>
      )}

      <ImportFromPsaDrawer
        open={showImport}
        onClose={() => setShowImport(false)}
        onChanged={() => refresh()}
      />

      {showAdd && (
        <AddUserDrawer
          roles={roles ?? []} departments={departments ?? []} boards={boards ?? []} templates={templates ?? []}
          onClose={() => setShowAdd(false)}
          onCreated={() => { refresh(); setShowAdd(false); }}
        />
      )}

    </div>
  );
}
