import { ThemeToggle } from '@/components/ThemeToggle';
import { QueryProvider } from '@/components/QueryProvider';
import { UserMenu } from '@/components/UserMenu';
import { TimerProvider, TimerWidget } from '@/components/TimerProvider';
import { NotificationsBell } from '@/components/NotificationsBell';
import { FileText, HelpCircle } from 'lucide-react';
import { HeaderSearch } from '@/components/HeaderSearch';
import { MobileNav } from '@/components/SidebarNav';
import { SidebarShell, SidebarProvider, SidebarToggle } from '@/components/SidebarShell';
import { UpdateWatchdog } from '@/components/UpdateWatchdog';
import { ViewAsBanner } from '@/components/ViewAs';



export default function DashboardLayout({ children }: { children: React.ReactNode }) {
  return (
    <QueryProvider>
    <TimerProvider>
    <UpdateWatchdog />
    <SidebarProvider>
    <div className="flex min-h-screen">
      <SidebarShell />

      {/* min-w-0: a flex item otherwise grows to its widest child's content, and the scrolling
          phone tab bar below made every dashboard page about 2,200px wide on a phone. */}
      <div className="flex min-w-0 flex-1 flex-col">
        <ViewAsBanner />
        <header className="flex h-14 items-center gap-3 border-b border-[var(--border)] bg-[var(--surface)] px-4 sm:px-6">
          <SidebarToggle />
          <HeaderSearch />
          <div className="ml-auto flex items-center gap-1.5 sm:gap-2">
            <TimerWidget />
            <NotificationsBell />
            <a href="/user-guide.pdf" target="_blank" rel="noopener noreferrer" className="hidden items-center gap-1.5 rounded-lg px-2.5 py-2 text-sm text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)] sm:inline-flex">
              <HelpCircle size={17} /> Help
            </a>
            <ThemeToggle />
            <UserMenu />
          </div>
        </header>

        {/* Mobile navigation — the sidebar is hidden below md. */}
        <MobileNav />

        <main className="flex-1 bg-[var(--bg)] p-4 sm:p-6">{children}</main>

        <footer className="flex flex-wrap items-center justify-between gap-2 border-t border-[var(--border)] bg-[var(--surface)] px-4 py-3 text-xs text-[var(--muted)] sm:px-6">
          <span>Desk Portal · v0.1.0</span>
          <a href="/user-guide.pdf" target="_blank" rel="noopener noreferrer" className="inline-flex items-center gap-1.5 hover:text-[var(--fg)]">
            <FileText size={13} /> User Guide (PDF)
          </a>
        </footer>
      </div>
    </div>
    </SidebarProvider>
    </TimerProvider>
    </QueryProvider>
  );
}
