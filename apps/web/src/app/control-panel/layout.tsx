'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { useQuery } from '@tanstack/react-query';
import { QueryProvider } from '@/components/QueryProvider';
import { ThemeToggle } from '@/components/ThemeToggle';
import { UserMenu } from '@/components/UserMenu';
import { api, ApiError } from '@/lib/api';
import {
  Rocket, FileText, Users, Server, UserCheck, ArrowUpCircle, Clock, CalendarDays,
  Megaphone, BarChart3, LayoutDashboard, Lock, Palette, BookOpen, Bell, ShieldAlert,
} from 'lucide-react';

type Section = {
  key: string; href: string; label: string; icon: React.ElementType; live: boolean;
  /// Shown to every client user regardless of section grants — for surfaces scoped to the
  /// caller's own data (like notification history) where a grant concept doesn't apply.
  always?: boolean;
};

// Sections mirror the client control panel; keys match the API's ControlPanelSection camelCase names.
// CP-1 ships Ticket Instructions + Users as live; the rest are placeholders for CP-2 / CP-3.
const MANAGEMENT: Section[] = [
  { key: 'ticketInstructions', href: '/control-panel/instructions', label: 'Ticket Instructions', icon: FileText, live: true },
  { key: 'users', href: '/control-panel/users', label: 'Users', icon: Users, live: true },
  { key: 'approvers', href: '/control-panel/approvers', label: 'Approvers', icon: UserCheck, live: true },
  { key: 'escalation', href: '/control-panel/escalation', label: 'Escalation Procedures', icon: ArrowUpCircle, live: true },
  { key: 'businessHours', href: '/control-panel/business-hours', label: 'Business Hours', icon: Clock, live: true },
  { key: 'holidays', href: '/control-panel/holidays', label: 'Holidays', icon: CalendarDays, live: true },
  { key: 'announcements', href: '/control-panel/announcements', label: 'Announcements', icon: Megaphone, live: true },
  { key: 'knowledgeBase', href: '/control-panel/knowledge-base', label: 'Knowledge Base', icon: BookOpen, live: true },
];
const ACCOUNTS: Section[] = [
  { key: 'accounts', href: '/control-panel/accounts', label: 'Accounts & Devices', icon: Server, live: true },
  { key: 'reports', href: '/control-panel/reports', label: 'Reports', icon: BarChart3, live: true },
  { key: 'branding', href: '/control-panel/branding', label: 'Branding', icon: Palette, live: true },
  { key: 'notificationHistory', href: '/control-panel/notifications', label: 'Notification History', icon: Bell, live: true, always: true },
];

export default function ControlPanelLayout({ children }: { children: React.ReactNode }) {
  return (
    <QueryProvider>
      <Shell>{children}</Shell>
    </QueryProvider>
  );
}

function Shell({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();
  const { data: caps, error: capsError } = useQuery({ queryKey: ['cp-capabilities'], queryFn: api.cpCapabilities, retry: false });
  const allowed = new Set(caps?.sections ?? []);
  // Every Control Panel endpoint refuses an account that is not a client portal user - MSP staff,
  // most often. The shell used to ignore that refusal: it showed sections anyway, and each page then
  // failed in its own words or silently. Say it once, here, and don't mount pages that can't load.
  const notClientUser = capsError instanceof ApiError && capsError.status === 403;

  const renderGroup = (label: string, items: Section[]) => {
    if (notClientUser) return null;
    // A section is shown when the caller can access it (admins get every key). Until capabilities
    // load we optimistically show the two live CP-1 sections so the panel isn't blank.
    const visible = items.filter((s) => s.always || (caps ? allowed.has(s.key) : s.live));
    if (visible.length === 0) return null;
    return (
      <div className="space-y-1">
        <div className="px-3 pb-1 text-[10px] font-semibold uppercase tracking-wider text-[var(--faint)]">{label}</div>
        {visible.map((s) => {
          const active = s.live && pathname.startsWith(s.href);
          const cls = `flex items-center justify-between gap-3 rounded-lg px-3 py-2 text-sm transition-colors ${
            active ? 'bg-brand text-brand-fg' : 'text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)]'
          } ${s.live ? '' : 'cursor-default opacity-60'}`;
          const inner = (
            <>
              <span className="flex items-center gap-3"><s.icon size={18} /> {s.label}</span>
              {!s.live && <span className="rounded-full bg-[var(--bg)] px-1.5 py-0.5 text-[9px] font-semibold uppercase tracking-wide text-[var(--faint)]">Soon</span>}
            </>
          );
          return s.live
            ? <Link key={s.key} href={s.href} className={cls}>{inner}</Link>
            : <div key={s.key} className={cls} aria-disabled title="Coming soon">{inner}</div>;
        })}
      </div>
    );
  };

  return (
    <div className="flex min-h-screen">
      <aside className="hidden w-64 shrink-0 flex-col border-r border-[var(--border)] bg-[var(--surface)] p-4 md:flex">
        <div className="mb-6 flex items-center gap-2.5 px-2">
          <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-brand text-brand-fg"><Rocket size={18} /></div>
          <div>
            <div className="font-semibold leading-tight">Control Panel</div>
            <div className="text-[10px] text-[var(--muted)]">{caps?.companyName ?? 'Client portal'}</div>
          </div>
        </div>
        <nav className="flex-1 space-y-5">
          {renderGroup('Management', MANAGEMENT)}
          {renderGroup('Accounts', ACCOUNTS)}
        </nav>
        <Link href="/dashboard" className="mt-2 flex items-center gap-2 rounded-lg px-3 py-2 text-xs font-medium text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)]">
          <LayoutDashboard size={15} /> Admin Dashboard
        </Link>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex h-14 items-center gap-3 border-b border-[var(--border)] bg-[var(--surface)] px-4 sm:px-6">
          <div className="flex items-center gap-2 font-semibold md:hidden"><Rocket size={17} className="text-brand" /> Control Panel</div>
          {caps && (
            <span className="hidden items-center gap-1.5 rounded-full bg-[var(--bg)] px-2.5 py-1 text-xs text-[var(--muted)] md:inline-flex">
              {caps.isCompanyAdministrator
                ? <><Lock size={11} /> Company Administrator</>
                : <>Client User</>}
            </span>
          )}
          <div className="ml-auto flex items-center gap-1.5 sm:gap-2">
            <ThemeToggle />
            <UserMenu />
          </div>
        </header>

        {/* Mobile nav */}
        <nav aria-label="Primary" className="flex gap-1 overflow-x-auto border-b border-[var(--border)] bg-[var(--surface)] px-2 py-2 md:hidden">
          {[...MANAGEMENT, ...ACCOUNTS].filter((s) => !notClientUser && s.live && (!caps || allowed.has(s.key))).map((s) => (
            <Link key={s.key} href={s.href} className="flex shrink-0 items-center gap-1.5 rounded-lg px-3 py-1.5 text-xs font-medium text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)]">
              <s.icon size={15} /> {s.label}
            </Link>
          ))}
        </nav>

        {/* Pages wait for capabilities: mounted earlier, a staff account's pages each fired a request
            that was refused before the shell knew to replace them. */}
        <main className="flex-1 bg-[var(--bg)] p-4 sm:p-6">
          {notClientUser ? <NotForThisAccount /> : caps || capsError ? children : null}
        </main>
      </div>
    </div>
  );
}

/** The one explanation a non-client account gets, in place of pages that would each be refused. */
function NotForThisAccount() {
  return (
    <div className="mx-auto mt-6 max-w-lg rounded-xl border border-[var(--border)] bg-[var(--surface)] p-6">
      <h1 className="flex items-center gap-2 text-lg font-semibold">
        <ShieldAlert size={19} className="text-brand" /> The Control Panel is for your clients
      </h1>
      <p className="mt-2 text-sm text-[var(--muted)]">
        This is where a client&rsquo;s company administrator manages their own portal &mdash; who can raise
        tickets, ticket instructions, approvers, business hours, holidays and announcements. It opens
        for client portal accounts, so staff accounts can&rsquo;t use it.
      </p>
      <Link href="/dashboard"
        className="mt-4 inline-flex items-center gap-2 rounded-lg bg-brand px-3 py-2 text-sm font-medium text-brand-fg hover:opacity-90">
        <LayoutDashboard size={15} /> Back to the dashboard
      </Link>
    </div>
  );
}
