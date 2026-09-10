'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { useQuery } from '@tanstack/react-query';
import {
  LayoutDashboard, Ticket, Plug, Bell, User, BarChart3, Activity, ListChecks, ShieldCheck,
  SlidersHorizontal, HardDrive, Rocket, Users, Building2, KeyRound, type LucideIcon, Inbox, Sparkles, Clock,} from 'lucide-react';
import { api } from '@/lib/api';

// `permissions` is ANY-OF: "Productivity" is rightly visible to a technician who may only see
// their own numbers AND to a manager who holds only the team-wide permission.
type NavItem = { href: string; label: string; icon: LucideIcon; permissions?: string[] };
/**
 * One accent per section. Colour here is navigation, not decoration: a glance at the icon tint
 * tells you which part of the product you are in, and the active row is unmistakable without
 * reading it.
 *
 * Class strings are written out in full — Tailwind only ships classes it can see in the source, so
 * anything assembled at runtime silently arrives unstyled.
 */
type Tone = 'brand' | 'sky' | 'violet';

const TONE: Record<Tone, {
  dot: string; activeRow: string; activeText: string; activeTile: string; idleTile: string;
}> = {
  brand: {
    dot: 'bg-brand',
    activeRow: 'bg-brand/10 dark:bg-brand/20',
    activeText: 'text-brand dark:text-brand-soft',
    activeTile: 'bg-brand text-brand-fg shadow-sm',
    idleTile: 'bg-[var(--bg)] text-[var(--faint)] group-hover:bg-brand/10 group-hover:text-brand dark:group-hover:text-brand-soft',
  },
  sky: {
    dot: 'bg-sky-500',
    activeRow: 'bg-sky-50 dark:bg-sky-950/50',
    activeText: 'text-sky-700 dark:text-sky-300',
    activeTile: 'bg-sky-700 text-white shadow-sm',
    idleTile: 'bg-[var(--bg)] text-[var(--faint)] group-hover:bg-sky-100 group-hover:text-sky-700 dark:group-hover:bg-sky-950 dark:group-hover:text-sky-300',
  },
  violet: {
    dot: 'bg-violet-500',
    activeRow: 'bg-violet-50 dark:bg-violet-950/50',
    activeText: 'text-violet-700 dark:text-violet-300',
    activeTile: 'bg-violet-700 text-white shadow-sm',
    idleTile: 'bg-[var(--bg)] text-[var(--faint)] group-hover:bg-violet-100 group-hover:text-violet-700 dark:group-hover:bg-violet-950 dark:group-hover:text-violet-300',
  },
};



/**
 * Navigation filtered to what the signed-in user can actually open. A technician without
 * connection rights used to see PSA Connections, Field Mapping and Audit Log anyway — every click
 * a 403. Menus are the first statement of what a role can do; this makes that statement true.
 *
 * Permission keys mirror the [RequirePermission] on each page's backing endpoints, so the menu and
 * the API can't disagree. Items with no key (Tickets, Profile…) are for everyone.
 */
const NAV_GROUPS: { label: string | null; tone: Tone; items: NavItem[] }[] = [
  {
    label: null,
    tone: 'brand',
    items: [
      { href: '/dashboard', label: 'Overview', icon: LayoutDashboard },
      { href: '/dashboard/tickets', label: 'Tickets', icon: Ticket },
      { href: '/dashboard/analytics', label: 'Productivity', icon: BarChart3, permissions: ['productivity.own.view', 'productivity.team.view'] },
      { href: '/dashboard/analytics/technicians', label: 'Technician hours', icon: Clock, permissions: ['productivity.own.view', 'productivity.team.view'] },
      { href: '/dashboard/analytics/clients', label: 'Client workload', icon: Building2, permissions: ['productivity.team.view'] },
      { href: '/dashboard/analytics/coverage', label: 'Portal coverage', icon: Activity, permissions: ['productivity.team.view'] },
    ],
  },
  {
    label: 'Integrations',
    tone: 'brand',
    items: [
      { href: '/dashboard/connections', label: 'PSA Connections', icon: Plug, permissions: ['connections.view'] },
      { href: '/dashboard/assistant', label: 'Assistant', icon: Sparkles, permissions: ['connections.manage'] },
      { href: '/dashboard/mappings', label: 'Field Mapping', icon: SlidersHorizontal, permissions: ['mappings.view'] },
      { href: '/dashboard/health', label: 'Integration Health', icon: Activity, permissions: ['integration.health.view'] },
    ],
  },
  {
    label: 'Management',
    tone: 'sky',
    items: [
      { href: '/dashboard/users', label: 'Users', icon: Users, permissions: ['users.manage'] },
      { href: '/dashboard/roles', label: 'Roles & Permissions', icon: ShieldCheck, permissions: ['roles.manage'] },
      { href: '/dashboard/permissions', label: 'Effective Permissions', icon: KeyRound, permissions: ['roles.manage'] },
      { href: '/dashboard/departments', label: 'Departments & Teams', icon: Building2, permissions: ['users.manage'] },
      { href: '/dashboard/jobs', label: 'Background Jobs', icon: ListChecks, permissions: ['jobs.manage'] },
      { href: '/dashboard/audit', label: 'Audit Log', icon: ShieldCheck, permissions: ['audit.view'] },
      { href: '/dashboard/enquiries', label: 'Enquiries', icon: Inbox, permissions: ['enquiries.view'] },
      { href: '/dashboard/notifications', label: 'Notifications', icon: Bell },
      { href: '/dashboard/profile', label: 'Profile', icon: User },
    ],
  },
  {
    label: 'Client-facing',
    tone: 'violet',
    items: [
      { href: '/control-panel', label: 'Control Panel', icon: Rocket },
    ],
  },
];

/** Same permission filter for both navs, so mobile can never show what the sidebar hides. */
function useVisibleNav() {
  // Until permissions load, show only the permissionless items — flashing admin
  // links at a technician and then yanking them reads as broken.
  const { data: me } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  const held = new Set(me?.permissions ?? []);
  return (item: NavItem) => !item.permissions || item.permissions.some((p) => held.has(p));
}

/** Compact horizontal nav for below-md, filtered identically to the sidebar. */
export function MobileNav() {
  const pathname = usePathname();
  const can = useVisibleNav();
  return (
    <nav aria-label="Primary" className="flex gap-1 overflow-x-auto border-b border-[var(--border)] bg-[var(--surface)] px-2 py-2 md:hidden">
      {NAV_GROUPS.flatMap((g) => g.items.map((i) => ({ ...i, tone: g.tone }))).filter(can).map(({ href, label, icon: Icon, tone }) => {
        const active = pathname === href;
        const t = TONE[tone];
        return (
          <Link
            key={href}
            href={href}
            aria-current={active ? 'page' : undefined}
            className={`flex shrink-0 items-center gap-1.5 rounded-lg px-3 py-1.5 text-xs font-medium ${
              active ? `${t.activeRow} ${t.activeText}` : 'text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)]'
            }`}
          >
            <Icon size={15} aria-hidden="true" />
            {label}
          </Link>
        );
      })}
    </nav>
  );
}

export function SidebarNav({ collapsed = false }: { collapsed?: boolean }) {
  const pathname = usePathname();
  const can = useVisibleNav();

  return (
    <nav aria-label="Primary" className={`flex-1 ${collapsed ? 'space-y-3' : 'space-y-6'}`}>
      {NAV_GROUPS.map((g, gi) => {
        const visible = g.items.filter(can);
        if (visible.length === 0) return null; // a heading over nothing is clutter
        const t = TONE[g.tone];
        return (
          <div key={gi} className="space-y-0.5">
            {/* Collapsed, the group heading becomes a rule: the grouping still reads, without
                a truncated word pretending to be a label. */}
            {g.label && (collapsed ? (
              <div className="mx-auto mb-2 h-px w-7 bg-[var(--border)]" aria-hidden="true" />
            ) : (
              <div className="mb-2 flex items-center gap-2 px-3">
                <span className={`h-1.5 w-1.5 rounded-full ${t.dot}`} aria-hidden="true" />
                <span className="text-[10px] font-semibold uppercase tracking-[0.14em] text-[var(--faint)]">
                  {g.label}
                </span>
                <span className="h-px flex-1 bg-[var(--border)]" aria-hidden="true" />
              </div>
            ))}
            {visible.map(({ href, label, icon: Icon }) => {
              const active = pathname === href;
              return (
                <Link
                  key={href}
                  href={href}
                  aria-current={active ? 'page' : undefined}
                  // The label is the accessible name whether or not it is painted, so the icon-only
                  // rail stays navigable by screen reader; title gives sighted users the same word
                  // on hover.
                  aria-label={collapsed ? label : undefined}
                  title={collapsed ? label : undefined}
                  className={`group relative flex items-center rounded-xl py-2 text-sm transition-all ${
                    collapsed ? 'justify-center px-1' : 'gap-3 px-2.5'
                  } ${
                    active
                      ? `${t.activeRow} font-medium ${t.activeText}`
                      : 'text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)]'
                  }`}
                >
                  {/* The bar makes the current page findable at a glance, not just tinted. */}
                  {active && (
                    <span
                      className={`absolute left-0 top-1/2 h-5 w-1 -translate-y-1/2 rounded-r-full ${t.dot}`}
                      aria-hidden="true"
                    />
                  )}
                  <span
                    className={`flex h-8 w-8 shrink-0 items-center justify-center rounded-lg transition-colors ${
                      active ? t.activeTile : t.idleTile
                    }`}
                  >
                    <Icon size={16} aria-hidden="true" />
                  </span>
                  {!collapsed && <span className="truncate">{label}</span>}
                </Link>
              );
            })}
          </div>
        );
      })}
    </nav>
  );
}

/**
 * Real attachment-storage usage, replacing a hardcoded "68% · 6.8 GB of 10 GB" that was pure
 * decoration. No invented quota: the portal has no storage limit, so showing one manufactured a
 * problem for the user to worry about. Hidden entirely for roles that cannot read the figure.
 */
export function StorageUsage() {
  const { data, isError } = useQuery({ queryKey: ['storage-usage'], queryFn: api.storageUsage, retry: false, staleTime: 60_000 });
  if (isError || !data) return null;

  const fmt = (bytes: number) => {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
    return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
  };

  return (
    <div className="mt-4 rounded-xl border border-[var(--border)] bg-gradient-to-br from-brand/[0.07] to-transparent p-3">
      <div className="flex items-center gap-2.5">
        <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg bg-brand/10 text-brand dark:text-brand-soft">
          <HardDrive size={15} aria-hidden="true" />
        </span>
        <span className="min-w-0 flex-1">
          <span className="block text-[11px] text-[var(--muted)]">Attachment storage</span>
          <span className="block text-sm font-semibold tabular-nums">{fmt(data.usedBytes)}</span>
        </span>
      </div>
      {/* No bar and no percentage: the portal sets no quota, and drawing one would invent a limit
          for someone to worry about. */}
      <p className="mt-2 text-[11px] text-[var(--muted)]">
        {data.fileCount} file{data.fileCount === 1 ? '' : 's'} across {data.ticketCount} ticket{data.ticketCount === 1 ? '' : 's'}
      </p>
    </div>
  );
}
