'use client';

import { use, useRef, useState, type ReactNode } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ViewAsButton } from '@/components/ViewAs';
import { StaffSkillsEditor, WorkScheduleEditor } from '@/components/Workforce';
import {
  ArrowLeft, Camera, Check, Copy, MailQuestion, Power, ShieldCheck, Trash2, X,
} from 'lucide-react';
import {
  api, BoardAccessMode as BAM, PermissionScope as PS,
  type UserSummary, type BoardOption, type EffectivePermission,
} from '@/lib/api';

const humanizeRole = (r: string) =>
  r.replace(/^Msp/, 'MSP ').replace(/([a-z])([A-Z])/g, '$1 $2').replace(/ +/g, ' ').trim();

const boardModeLabel = (mode: number) =>
  mode === BAM.Selected ? 'Selected boards' : mode === BAM.None ? 'No board access' : 'All boards';

const SCOPE_LABEL: Record<number, string> = {
  [PS.All]: 'All',
  [PS.Department]: 'Department',
  [PS.Team]: 'Team',
  [PS.Assigned]: 'Assigned to them',
  [PS.Own]: 'Own only',
  [PS.Selected]: 'Selected',
  [PS.None]: 'None',
};

const SOURCE_LABEL: Record<string, { label: string; tone: string }> = {
  NoGrant: { label: 'Not granted', tone: 'text-[var(--faint)]' },
  RoleGrant: { label: 'Via role', tone: 'text-[var(--fg)]' },
  OverrideGrant: { label: 'Override — granted', tone: 'text-green-700 dark:text-green-400' },
  OverrideDeny: { label: 'Override — denied', tone: 'text-red-600 dark:text-red-400' },
};

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

const TABS = [
  { key: 'overview', label: 'Overview' },
  { key: 'permissions', label: 'Permissions' },
  { key: 'departments', label: 'Departments' },
  { key: 'teams', label: 'Teams' },
  { key: 'boards', label: 'Boards' },
  { key: 'psa', label: 'PSA identity' },
  { key: 'schedule', label: 'Work schedule' },
  { key: 'skills', label: 'Skills' },
  { key: 'activity', label: 'Activity' },
  { key: 'security', label: 'Security' },
] as const;
type TabKey = typeof TABS[number]['key'];

function PhotoPicker({ userId, current, onChanged }: { userId: string; current: string | null; onChanged: (url: string | null) => void }) {
  const inputRef = useRef<HTMLInputElement>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function pick(file: File | undefined) {
    if (!file) return;
    setError(null);
    setBusy(true);
    try {
      const updated = await api.uploadUserPhoto(userId, file);
      onChanged(updated.photoUrl);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not upload the photo.');
    } finally {
      setBusy(false);
      if (inputRef.current) inputRef.current.value = '';
    }
  }

  async function clear() {
    setBusy(true);
    try {
      await api.removeUserPhoto(userId);
      onChanged(null);
    } catch {
      setError('Could not remove the photo.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <div>
      <div className="flex flex-wrap items-center gap-3">
        <span className="flex h-16 w-16 shrink-0 items-center justify-center overflow-hidden rounded-full border border-[var(--border)] bg-brand/10 text-lg font-semibold text-brand">
          {current ? (
            // eslint-disable-next-line @next/next/no-img-element
            <img src={current} alt="" className="h-full w-full object-cover" />
          ) : (
            <Camera size={20} className="text-[var(--faint)]" aria-hidden="true" />
          )}
        </span>
        <input ref={inputRef} type="file" accept="image/png,image/jpeg,image/webp,image/gif"
          className="sr-only" onChange={(e) => pick(e.target.files?.[0])} />
        <button type="button" disabled={busy} onClick={() => inputRef.current?.click()}
          className="inline-flex items-center gap-2 rounded-lg border border-[var(--border)] px-3 py-2 text-sm font-medium hover:bg-[var(--bg)] disabled:opacity-50">
          {busy ? 'Uploading…' : current ? 'Change photo' : 'Upload photo'}
        </button>
        {current && (
          <button type="button" disabled={busy} onClick={clear} className="text-sm text-[var(--muted)] hover:text-red-600 disabled:opacity-50">
            Remove
          </button>
        )}
      </div>
      <p className="mt-2 text-xs text-[var(--muted)]">PNG, JPEG, WebP or GIF, up to 1 MB.</p>
      {error && <p className="mt-1.5 text-xs text-red-600 dark:text-red-400">{error}</p>}
    </div>
  );
}

function SectionHeading({ children }: { children: ReactNode }) {
  return <h3 className="text-xs font-semibold uppercase tracking-wide text-[var(--faint)]">{children}</h3>;
}

export default function UserDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params);
  const qc = useQueryClient();
  const router = useRouter();
  const [tab, setTab] = useState<TabKey>('overview');
  const [deleteError, setDeleteError] = useState<string | null>(null);

  const { data: user, isLoading, isError } = useQuery({ queryKey: ['staff-user', id], queryFn: () => api.staffUser(id) });
  const { data: roles } = useQuery({ queryKey: ['staff-roles'], queryFn: api.staffRoles, staleTime: 5 * 60_000 });
  const { data: departments } = useQuery({ queryKey: ['staff-departments'], queryFn: api.staffDepartments, staleTime: 5 * 60_000 });
  const { data: me } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  // The workforce tabs only where the module is switched on.
  const tabs = TABS.filter((t) => (t.key !== 'schedule' && t.key !== 'skills') || me?.features?.workforce === true);
  const { data: boards } = useQuery({ queryKey: ['staff-boards'], queryFn: api.staffBoards, staleTime: 60_000 });
  const { data: templates } = useQuery({ queryKey: ['permission-templates'], queryFn: api.permissionTemplates, staleTime: 5 * 60_000 });
  const { data: permissions, isLoading: permsLoading } = useQuery({
    queryKey: ['user-permissions', id], queryFn: () => api.userEffectivePermissions(id), enabled: tab === 'permissions',
  });
  const { data: activity, isLoading: activityLoading, isError: activityError } = useQuery({
    queryKey: ['user-audit', id], queryFn: () => api.userAuditLog(id), enabled: tab === 'activity',
  });

  const refresh = () => { qc.invalidateQueries({ queryKey: ['staff-user', id] }); qc.invalidateQueries({ queryKey: ['staff-users'] }); };

  if (isLoading) return <p className="px-1 text-sm text-[var(--muted)]">Loading…</p>;
  if (isError || !user) {
    return (
      <div className="space-y-4">
        <Link href="/dashboard/users" className="inline-flex items-center gap-1.5 text-sm text-[var(--muted)] hover:text-[var(--fg)]">
          <ArrowLeft size={16} /> Back to users
        </Link>
        <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] px-5 py-8 text-center">
          <p className="text-sm font-medium">User not found.</p>
        </div>
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-3xl space-y-5">
      <Link href="/dashboard/users" className="inline-flex items-center gap-1.5 text-sm text-[var(--muted)] hover:text-[var(--fg)]">
        <ArrowLeft size={16} /> Back to users
      </Link>

      <UserHeader user={user} onChanged={refresh} onDeleted={() => router.push('/dashboard/users')} onDeleteError={setDeleteError} />
      {deleteError && <p className="text-xs text-red-600 dark:text-red-400">{deleteError}</p>}
      {user.isActive && <ViewAsButton personKey={`u:${user.id}`} name={user.displayName} />}

      <div className="flex flex-wrap gap-1 border-b border-[var(--border)]">
        {tabs.map((t) => (
          <button key={t.key} onClick={() => setTab(t.key)}
            className={`border-b-2 px-3 py-2 text-sm font-medium ${tab === t.key
              ? 'border-brand text-brand'
              : 'border-transparent text-[var(--muted)] hover:text-[var(--fg)]'}`}>
            {t.label}
          </button>
        ))}
      </div>

      <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-5">
        {tab === 'overview' && <OverviewTab user={user} roles={roles ?? []} onChanged={refresh} />}
        {tab === 'permissions' && (
          <PermissionsTab
            userId={id} templates={templates ?? []} permissions={permissions} loading={permsLoading} onChanged={refresh}
          />
        )}
        {tab === 'departments' && <DepartmentsTab userId={id} user={user} departments={departments ?? []} onChanged={refresh} />}
        {tab === 'teams' && <TeamsTab userId={id} user={user} departments={departments ?? []} onChanged={refresh} />}
        {tab === 'boards' && <BoardsTab userId={id} user={user} boards={boards ?? []} onChanged={refresh} />}
        {tab === 'psa' && <PsaIdentityTab userId={id} />}
        {tab === 'schedule' && <WorkScheduleEditor userId={id} />}
        {tab === 'skills' && <StaffSkillsEditor userId={id} canManage={!!me?.permissions.includes('workforce.manage')} />}
        {tab === 'activity' && <ActivityTab entries={activity} loading={activityLoading} error={activityError} />}
        {tab === 'security' && <SecurityTab user={user} />}
      </div>
    </div>
  );
}

function UserHeader({ user, onChanged, onDeleted, onDeleteError }: {
  user: UserSummary; onChanged: () => void; onDeleted: () => void; onDeleteError: (e: string | null) => void;
}) {
  const setActive = useMutation({ mutationFn: (active: boolean) => api.setUserActive(user.id, active), onSuccess: onChanged });
  const del = useMutation({
    mutationFn: () => api.deleteStaffUser(user.id),
    onSuccess: () => { onDeleteError(null); onDeleted(); },
    onError: (e) => onDeleteError(e instanceof Error ? e.message : 'Could not delete this user.'),
  });
  const copyInstructions = () => {
    navigator.clipboard.writeText(`Sign in to the portal at ${window.location.origin} using your work account: ${user.email}`);
  };

  return (
    <div className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-5">
      <div className="flex items-center gap-3">
        {user.photoUrl ? (
          // eslint-disable-next-line @next/next/no-img-element
          <img src={user.photoUrl} alt="" className="h-12 w-12 shrink-0 rounded-full object-cover" />
        ) : (
          <span className="flex h-12 w-12 shrink-0 items-center justify-center rounded-full bg-brand/10 text-lg font-semibold text-brand">
            {user.displayName.slice(0, 1).toUpperCase()}
          </span>
        )}
        <div>
          <h1 className="text-lg font-semibold">{user.displayName}</h1>
          <p className="text-sm text-[var(--muted)]">{user.email}</p>
        </div>
        {!user.signInLinked && (
          <span title="Created here, but they have not signed in yet."
            className="inline-flex items-center gap-1 rounded bg-amber-100 px-1.5 py-0.5 text-[11px] font-medium text-amber-700 dark:bg-amber-950 dark:text-amber-300">
            <MailQuestion size={11} /> Pending
          </span>
        )}
        {!user.isActive && (
          <span className="rounded bg-slate-200 px-1.5 py-0.5 text-[11px] font-medium text-slate-600 dark:bg-slate-800 dark:text-slate-300">
            Deactivated
          </span>
        )}
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <button onClick={copyInstructions}
          className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium hover:bg-[var(--bg)]">
          <Copy size={13} /> Copy sign-in instructions
        </button>
        <button onClick={() => setActive.mutate(!user.isActive)} disabled={setActive.isPending}
          className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium hover:bg-[var(--bg)]">
          <Power size={13} /> {user.isActive ? 'Deactivate' : 'Reactivate'}
        </button>
        <button
          onClick={() => { if (window.confirm(`Delete ${user.displayName}? This removes their account and all role, department, and board assignments.`)) del.mutate(); }}
          disabled={del.isPending}
          className="inline-flex items-center gap-1.5 rounded-lg border border-red-300 px-2.5 py-1.5 text-xs font-medium text-red-600 hover:bg-red-50 disabled:opacity-50 dark:hover:bg-red-950/40">
          <Trash2 size={13} /> Delete
        </button>
      </div>
    </div>
  );
}

function OverviewTab({ user, roles, onChanged }: { user: UserSummary; roles: { id: string; name: string }[]; onChanged: () => void }) {
  const [name, setName] = useState(user.displayName);
  const [email, setEmail] = useState(user.email);
  const [phone, setPhone] = useState(user.phoneNumber ?? '');
  const [location, setLocation] = useState(user.location ?? '');

  const saveBasics = useMutation({
    mutationFn: () => api.updateStaffUser(user.id, {
      displayName: name.trim(), email: email.trim(), phoneNumber: phone.trim() || null, location: location.trim() || null,
    }),
    onSuccess: onChanged,
  });

  const held = new Set(user.roles.map((r) => r.id));
  const addable = roles.filter((r) => !held.has(r.id));
  const addRole = useMutation({ mutationFn: (roleId: string) => api.assignUserRole(user.id, roleId), onSuccess: onChanged });
  const dropRole = useMutation({ mutationFn: (roleId: string) => api.removeUserRole(user.id, roleId), onSuccess: onChanged });
  const roleError = dropRole.error ?? addRole.error;

  return (
    <div className="space-y-6">
      <section className="space-y-3">
        <SectionHeading>Photo</SectionHeading>
        <PhotoPicker userId={user.id} current={user.photoUrl} onChanged={onChanged} />
      </section>

      <section className="space-y-3">
        <SectionHeading>Basic info</SectionHeading>
        <div className="grid gap-3 sm:grid-cols-2">
          <label className="block">
            <span className="mb-1 block text-xs font-medium">Full name</span>
            <input value={name} onChange={(e) => setName(e.target.value)}
              className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand" />
          </label>
          <label className="block">
            <span className="mb-1 block text-xs font-medium">Email</span>
            <input type="email" value={email} onChange={(e) => setEmail(e.target.value)}
              className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand" />
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
        </div>
        <div className="flex items-center gap-2">
          <button type="button" onClick={() => saveBasics.mutate()} disabled={saveBasics.isPending}
            className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-3 py-1.5 text-xs font-medium hover:bg-[var(--bg)] disabled:opacity-50">
            {saveBasics.isPending ? 'Saving…' : 'Save'}
          </button>
          {saveBasics.isSuccess && !saveBasics.isPending && (
            <span className="inline-flex items-center gap-1 text-xs text-green-700 dark:text-green-400"><Check size={12} /> Saved</span>
          )}
        </div>
        {saveBasics.isError && <p className="text-xs text-red-600 dark:text-red-400">
          {saveBasics.error instanceof Error ? saveBasics.error.message : 'Could not save.'}</p>}
      </section>

      <section className="space-y-2">
        <SectionHeading>Roles</SectionHeading>
        <div className="flex flex-wrap gap-1.5">
          {user.roles.map((r) => (
            <span key={r.id} className="inline-flex items-center gap-1 rounded-full border border-[var(--border)] bg-[var(--bg)] px-2 py-0.5 text-[11px] font-medium">
              <ShieldCheck size={11} className="text-brand" /> {humanizeRole(r.name)}
              {user.roles.length > 1 && (
                <button onClick={() => dropRole.mutate(r.id)} aria-label={`Remove ${humanizeRole(r.name)} role`}
                  className="text-[var(--muted)] hover:text-red-600"><X size={11} /></button>
              )}
            </span>
          ))}
          {addable.length > 0 && (
            <select value="" onChange={(e) => { if (e.target.value) addRole.mutate(e.target.value); }} aria-label="Add role"
              className="rounded-full border border-dashed border-[var(--border)] bg-transparent px-2 py-0.5 text-[11px] text-[var(--muted)]">
              <option value="">+ role</option>
              {addable.map((r) => <option key={r.id} value={r.id}>{humanizeRole(r.name)}</option>)}
            </select>
          )}
        </div>
        {roleError && <p className="text-xs text-red-600 dark:text-red-400">
          {roleError instanceof Error ? roleError.message : 'Could not change roles.'}</p>}
      </section>

      <section className="grid grid-cols-2 gap-3 text-xs text-[var(--muted)] sm:grid-cols-4">
        <div><span className="block text-[var(--faint)]">Status</span>{user.isActive ? 'Active' : 'Deactivated'}</div>
        <div><span className="block text-[var(--faint)]">Sign-in</span>{user.signInLinked ? 'Linked' : 'Pending'}</div>
        <div><span className="block text-[var(--faint)]">Last active</span>{relativeTime(user.lastActiveAt)}</div>
        <div><span className="block text-[var(--faint)]">Created</span>{new Date(user.createdAt).toLocaleDateString()}</div>
      </section>
    </div>
  );
}

function PermissionsTab({ userId, templates, permissions, loading, onChanged }: {
  userId: string; templates: { id: string; name: string; description: string | null }[];
  permissions: EffectivePermission[] | undefined; loading: boolean; onChanged: () => void;
}) {
  const [templateId, setTemplateId] = useState('');
  const applyTemplate = useMutation({ mutationFn: () => api.applyPermissionTemplate(userId, templateId), onSuccess: onChanged });

  const grouped = (permissions ?? []).reduce<Record<string, EffectivePermission[]>>((acc, p) => {
    (acc[p.module] ??= []).push(p);
    return acc;
  }, {});

  return (
    <div className="space-y-6">
      <section className="space-y-2">
        <SectionHeading>Apply permission template</SectionHeading>
        <div className="flex gap-2">
          <select value={templateId} onChange={(e) => setTemplateId(e.target.value)}
            className="flex-1 rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand">
            <option value="">Choose a template…</option>
            {templates.map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
          </select>
          <button type="button" onClick={() => applyTemplate.mutate()} disabled={!templateId || applyTemplate.isPending}
            className="rounded-lg bg-brand px-3 py-2 text-xs font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
            {applyTemplate.isPending ? 'Applying…' : 'Apply'}
          </button>
        </div>
        <p className="text-xs text-[var(--muted)]">Applies the template&apos;s differences on top of this user&apos;s current roles.</p>
        {applyTemplate.isError && <p className="text-xs text-red-600 dark:text-red-400">
          {applyTemplate.error instanceof Error ? applyTemplate.error.message : 'Could not apply the template.'}</p>}
      </section>

      <section className="space-y-3">
        <SectionHeading>Effective permissions</SectionHeading>
        {loading && <p className="text-sm text-[var(--muted)]">Loading…</p>}
        {!loading && Object.keys(grouped).length === 0 && <p className="text-sm text-[var(--muted)]">No permissions found.</p>}
        {Object.entries(grouped).map(([module, perms]) => (
          <div key={module}>
            <h4 className="mb-1.5 text-xs font-semibold text-[var(--fg)]">{module}</h4>
            <div className="overflow-hidden rounded-lg border border-[var(--border)]">
              <table className="w-full text-xs">
                <tbody>
                  {perms.map((p) => {
                    const source = SOURCE_LABEL[p.source] ?? { label: p.source, tone: 'text-[var(--muted)]' };
                    return (
                      <tr key={p.permissionKey} className="border-b border-[var(--border)] last:border-0">
                        <td className="px-3 py-2 font-medium">{p.displayName}</td>
                        <td className="px-3 py-2 text-[var(--muted)]">{SCOPE_LABEL[p.scope] ?? p.scope}</td>
                        <td className={`px-3 py-2 ${source.tone}`}>{source.label}</td>
                        {p.isBoardAware && <td className="px-3 py-2 text-[var(--faint)]">{p.boardAccessMode}</td>}
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </div>
        ))}
      </section>
    </div>
  );
}

function DepartmentsTab({ userId, user, departments, onChanged }: {
  userId: string; user: UserSummary; departments: { id: string; name: string }[]; onChanged: () => void;
}) {
  const [primaryId, setPrimaryId] = useState(user.primaryDepartment?.id ?? '');
  const setPrimary = useMutation({
    mutationFn: async (deptId: string) => {
      if (user.primaryDepartment && user.primaryDepartment.id !== deptId) await api.removeUserDepartment(userId, user.primaryDepartment.id);
      if (deptId) await api.setUserDepartment(userId, deptId, true);
    },
    onSuccess: onChanged,
  });

  const secondaryIds = new Set(user.secondaryDepartments.map((d) => d.id));
  const addableSecondary = departments.filter((d) => d.id !== primaryId && !secondaryIds.has(d.id));
  const addSecondary = useMutation({ mutationFn: (deptId: string) => api.setUserDepartment(userId, deptId, false), onSuccess: onChanged });
  const dropSecondary = useMutation({ mutationFn: (deptId: string) => api.removeUserDepartment(userId, deptId), onSuccess: onChanged });

  return (
    <div className="space-y-6">
      <section className="space-y-2">
        <SectionHeading>Primary department</SectionHeading>
        <select value={primaryId} onChange={(e) => { setPrimaryId(e.target.value); setPrimary.mutate(e.target.value); }}
          className="w-full max-w-xs rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand">
          <option value="">Not set</option>
          {departments.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
        </select>
      </section>

      <section className="space-y-2">
        <SectionHeading>Other departments</SectionHeading>
        <div className="flex flex-wrap gap-1.5">
          {user.secondaryDepartments.map((d) => (
            <span key={d.id} className="inline-flex items-center gap-1 rounded-full border border-[var(--border)] bg-[var(--bg)] px-2 py-0.5 text-[11px] font-medium">
              {d.name}
              <button onClick={() => dropSecondary.mutate(d.id)} aria-label={`Remove ${d.name}`} className="text-[var(--muted)] hover:text-red-600"><X size={11} /></button>
            </span>
          ))}
          {addableSecondary.length > 0 && (
            <select value="" onChange={(e) => { if (e.target.value) addSecondary.mutate(e.target.value); }} aria-label="Add department"
              className="rounded-full border border-dashed border-[var(--border)] bg-transparent px-2 py-0.5 text-[11px] text-[var(--muted)]">
              <option value="">+ department</option>
              {addableSecondary.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
            </select>
          )}
        </div>
        <p className="text-xs text-[var(--muted)]">Secondary memberships beyond the primary department, e.g. for cross-team staff.</p>
      </section>
    </div>
  );
}

function TeamsTab({ userId, user, departments, onChanged }: {
  userId: string; user: UserSummary; departments: { id: string; name: string; teams: { id: string; name: string; departmentId: string }[] }[]; onChanged: () => void;
}) {
  const allTeams = departments.flatMap((d) => d.teams);
  const heldTeams = new Set(user.teams.map((t) => t.id));
  const addableTeams = allTeams.filter((t) => !heldTeams.has(t.id));
  const addTeam = useMutation({ mutationFn: (teamId: string) => api.assignUserTeam(userId, teamId), onSuccess: onChanged });
  const dropTeam = useMutation({ mutationFn: (teamId: string) => api.removeUserTeam(userId, teamId), onSuccess: onChanged });

  return (
    <section className="space-y-2">
      <SectionHeading>Teams</SectionHeading>
      <div className="flex flex-wrap gap-1.5">
        {user.teams.map((t) => (
          <span key={t.id} className="inline-flex items-center gap-1 rounded-full border border-[var(--border)] bg-[var(--bg)] px-2 py-0.5 text-[11px] font-medium">
            {t.name}
            <button onClick={() => dropTeam.mutate(t.id)} aria-label={`Remove ${t.name}`} className="text-[var(--muted)] hover:text-red-600"><X size={11} /></button>
          </span>
        ))}
        {user.teams.length === 0 && addableTeams.length === 0 && <span className="text-xs text-[var(--faint)]">No teams available.</span>}
        {addableTeams.length > 0 && (
          <select value="" onChange={(e) => { if (e.target.value) addTeam.mutate(e.target.value); }} aria-label="Add team"
            className="rounded-full border border-dashed border-[var(--border)] bg-transparent px-2 py-0.5 text-[11px] text-[var(--muted)]">
            <option value="">+ team</option>
            {addableTeams.map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
          </select>
        )}
      </div>
      <p className="text-xs text-[var(--muted)]">Teams span every department — pick any of them, not just the primary one.</p>
    </section>
  );
}

/**
 * Who this person is inside each PSA — what their logged time is attributed to.
 *
 * One row per connection, because the same technician has a different identifier in every PSA and
 * the identifiers are not the same kind of value: Autotask takes a numeric resource id, ConnectWise
 * a member identifier. Unmapped is a legitimate state, and says what it means rather than looking
 * like something failed: their time goes to the connection's default resource.
 */
function PsaIdentityTab({ userId }: { userId: string }) {
  const qc = useQueryClient();
  const { data, isLoading, error } = useQuery({
    queryKey: ['user-psa-identities', userId],
    queryFn: () => api.userPsaIdentities(userId),
    retry: false,
  });
  const save = useMutation({
    mutationFn: ({ connectionId, techId }: { connectionId: string; techId: string | null }) =>
      api.setUserPsaIdentity(userId, connectionId, techId),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['user-psa-identities', userId] }),
  });

  if (isLoading) return <p className="text-sm text-[var(--muted)]">Loading…</p>;
  if (error) return <p className="text-sm text-red-600 dark:text-red-400">{(error as Error).message}</p>;
  if (!data?.length) return <p className="text-sm text-[var(--muted)]">No enabled PSA connections.</p>;

  return (
    <div className="space-y-4">
      <p className="max-w-prose text-sm text-[var(--muted)]">
        Time this person logs from the portal is recorded against the resource chosen here. Leave a
        connection unset and their time is attributed to that connection&rsquo;s default resource instead.
      </p>
      <ul className="space-y-3">
        {data.map((row) => (
          <li key={row.psaConnectionId}
            className="flex flex-wrap items-center gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] px-4 py-3">
            <span className="w-28 shrink-0 text-sm font-medium">{row.connectionName}</span>
            {row.technicians.length === 0 ? (
              <span className="flex flex-wrap items-center gap-2 text-xs text-[var(--faint)]">
                {row.externalTechnicianId
                  ? `Mapped to ${row.externalTechnicianName ?? row.externalTechnicianId} — the PSA's technician list is unavailable, so it can be removed but not changed right now.`
                  : 'Technician list unavailable from this PSA right now.'}
                {/* Removing a link asks nothing of the PSA, so it works while the PSA is unreachable. */}
                {row.externalTechnicianId && (
                  <button type="button" disabled={save.isPending}
                    onClick={() => save.mutate({ connectionId: row.psaConnectionId, techId: null })}
                    className="rounded-lg border border-[var(--border)] px-2.5 py-1 text-xs font-medium text-[var(--fg)] hover:bg-[var(--bg)] disabled:opacity-50">
                    {save.isPending ? 'Removing…' : 'Remove link'}
                  </button>
                )}
              </span>
            ) : (
              <select
                value={row.externalTechnicianId ?? ''}
                disabled={save.isPending}
                onChange={(e) => save.mutate({ connectionId: row.psaConnectionId, techId: e.target.value || null })}
                className="min-w-56 rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2.5 py-1.5 text-sm outline-none focus:border-brand disabled:opacity-50">
                <option value="">Not mapped — use the connection default</option>
                {row.technicians.map((t) => (
                  <option key={t.value} value={t.value}>{t.label}</option>
                ))}
              </select>
            )}
            {row.externalTechnicianId && (
              <span className="rounded-full bg-brand-tint px-2 py-0.5 text-[11px] font-medium text-brand dark:bg-brand/20">
                id {row.externalTechnicianId}
              </span>
            )}
          </li>
        ))}
      </ul>
      {save.isError && (
        <p className="text-sm text-red-600 dark:text-red-400">{(save.error as Error).message}</p>
      )}
    </div>
  );
}

function BoardsTab({ userId, user, boards, onChanged }: { userId: string; user: UserSummary; boards: BoardOption[]; onChanged: () => void }) {
  const setMode = useMutation({ mutationFn: (mode: number) => api.setUserBoardAccessMode(userId, mode), onSuccess: onChanged });
  const heldBoards = new Set(user.boardGrants.map((b) => `${b.psaConnectionId}::${b.boardName}`));
  const grantBoard = useMutation({
    mutationFn: (b: BoardOption) => api.setUserBoardGrant(userId, { psaConnectionId: b.psaConnectionId, boardName: b.boardName, actions: 1 }),
    onSuccess: onChanged,
  });
  const revokeBoard = useMutation({
    mutationFn: (b: BoardOption) => api.removeUserBoardGrant(userId, b.psaConnectionId, b.boardName),
    onSuccess: onChanged,
  });

  return (
    <section className="space-y-3">
      <SectionHeading>Board access</SectionHeading>
      <div className="flex gap-1.5">
        {[{ v: BAM.All, l: 'All boards' }, { v: BAM.Selected, l: 'Selected' }, { v: BAM.None, l: 'None' }].map((m) => (
          <button key={m.v} type="button" onClick={() => setMode.mutate(m.v)}
            className={`rounded-full border px-2.5 py-1 text-xs font-medium ${user.boardAccessMode === m.v
              ? 'border-brand bg-brand/10 text-brand' : 'border-[var(--border)] text-[var(--muted)] hover:bg-[var(--bg)]'}`}>
            {m.l}
          </button>
        ))}
      </div>
      <p className="text-xs text-[var(--muted)]">Currently: {boardModeLabel(user.boardAccessMode)}.</p>
      {user.boardAccessMode === BAM.Selected && (
        <div className="max-h-64 space-y-1 overflow-y-auto rounded-lg border border-[var(--border)] p-2">
          {boards.length === 0 && <p className="text-xs text-[var(--muted)]">No boards found — sync a PSA connection first.</p>}
          {boards.map((b) => {
            const key = `${b.psaConnectionId}::${b.boardName}`;
            const granted = heldBoards.has(key);
            return (
              <label key={key} className="flex items-center gap-2 text-xs">
                <input type="checkbox" checked={granted} onChange={() => (granted ? revokeBoard.mutate(b) : grantBoard.mutate(b))} />
                {b.boardName} <span className="text-[var(--faint)]">({b.connectionName})</span>
              </label>
            );
          })}
        </div>
      )}
    </section>
  );
}

function ago(iso: string): string {
  const s = Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 1000));
  if (s < 60) return `${s}s ago`;
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} hr ago`;
  return `${Math.floor(s / 86400)}d ago`;
}

function ActivityTab({ entries, loading, error }: { entries: { id: string; action: string; actorDisplayName: string | null; createdAt: string }[] | undefined; loading: boolean; error: boolean }) {
  return (
    <section className="space-y-2">
      <SectionHeading>Recent activity</SectionHeading>
      {loading && <p className="text-sm text-[var(--muted)]">Loading…</p>}
      {error && <p className="text-sm text-[var(--muted)]">You don&apos;t have permission to view audit history, or it could not be loaded.</p>}
      {!loading && !error && (entries?.length ?? 0) === 0 && <p className="text-sm text-[var(--muted)]">No recorded activity for this user yet.</p>}
      {!loading && !error && entries && entries.length > 0 && (
        <ul className="divide-y divide-[var(--border)] rounded-lg border border-[var(--border)]">
          {entries.map((e) => (
            <li key={e.id} className="flex items-center justify-between px-3 py-2 text-sm">
              <span>
                <span className="font-medium">{e.action}</span>
                {e.actorDisplayName && <span className="text-[var(--muted)]"> · by {e.actorDisplayName}</span>}
              </span>
              <span className="text-xs text-[var(--muted)]" title={new Date(e.createdAt).toLocaleString()}>{ago(e.createdAt)}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

function SecurityTab({ user }: { user: UserSummary }) {
  return (
    <div className="space-y-6">
      <section className="grid grid-cols-2 gap-4 text-sm sm:grid-cols-3">
        <div>
          <span className="block text-xs text-[var(--faint)]">Sign-in</span>
          {user.signInLinked ? 'Linked to identity provider' : 'Awaiting first sign-in'}
        </div>
        <div>
          <span className="block text-xs text-[var(--faint)]">Account status</span>
          {user.isActive ? 'Active' : 'Deactivated — sign-in blocked'}
        </div>
        <div>
          <span className="block text-xs text-[var(--faint)]">Last active</span>
          {relativeTime(user.lastActiveAt)}
          <span className="block text-[10px] text-[var(--faint)]">Last authenticated request seen — not a login-event log.</span>
        </div>
      </section>

      <section className="space-y-1.5 rounded-lg border border-[var(--border)] bg-[var(--bg)] p-4">
        <h4 className="text-sm font-medium">Multi-factor authentication</h4>
        <p className="text-xs text-[var(--muted)]">
          Managed by your identity provider — not available here. MFA enrollment and enforcement are
          configured directly in your organization&apos;s Keycloak realm.
        </p>
      </section>

      <section className="space-y-1.5 rounded-lg border border-[var(--border)] bg-[var(--bg)] p-4">
        <h4 className="text-sm font-medium">Password</h4>
        <p className="text-xs text-[var(--muted)]">
          There is no portal password — sign-in is delegated entirely to your identity provider, so
          there is nothing to reset here.
        </p>
      </section>
    </div>
  );
}
