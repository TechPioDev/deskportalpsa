'use client';

import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import { ChevronLeft, ChevronRight, PanelLeft } from 'lucide-react';
import { SidebarNav, StorageUsage } from '@/components/SidebarNav';
import { BrandMark } from '@/components/BrandMark';

/**
 * The desk sidebar, collapsed to icons by default.
 *
 * A technician works inside a ticket, not inside the navigation — and with the ticket page carrying
 * its own properties rail, a permanently expanded 256px nav spent a fifth of the screen repeating
 * where you already are. Collapsed it is 68px: every destination still one click away, just without
 * the labels.
 *
 * The state lives in a context rather than in the sidebar itself so the HEADER can own the control.
 * A toggle at the foot of the nav sits below every item and is missed; one pinned to the top-left of
 * the header is where people already look for it, and stays put no matter how far the page scrolls.
 */
const KEY = 'desk.sidebar.collapsed';

type SidebarState = { collapsed: boolean; toggle: () => void };
const SidebarContext = createContext<SidebarState>({ collapsed: true, toggle: () => {} });

export function SidebarProvider({ children }: { children: React.ReactNode }) {
  // Default collapsed. The stored preference is read AFTER mount: reading localStorage during
  // render makes server and client disagree about the first paint, which React resolves by
  // throwing the markup away.
  const [collapsed, setCollapsed] = useState(true);

  useEffect(() => {
    try {
      const saved = window.localStorage.getItem(KEY);
      if (saved !== null) setCollapsed(saved === '1');
    } catch {
      // Private windows and blocked site data throw on access; the default stands.
    }
  }, []);

  const toggle = useCallback(() => {
    setCollapsed((prev) => {
      const next = !prev;
      try { window.localStorage.setItem(KEY, next ? '1' : '0'); } catch { /* the preference is a nicety */ }
      return next;
    });
  }, []);

  const value = useMemo(() => ({ collapsed, toggle }), [collapsed, toggle]);
  return <SidebarContext.Provider value={value}>{children}</SidebarContext.Provider>;
}

/** Header control: always on screen, so hiding the nav is never a hunt. */
export function SidebarToggle() {
  const { collapsed, toggle } = useContext(SidebarContext);
  return (
    <button
      type="button"
      onClick={toggle}
      aria-expanded={!collapsed}
      aria-label={collapsed ? 'Show menu labels' : 'Hide menu labels'}
      title={collapsed ? 'Show menu' : 'Hide menu'}
      className="hidden shrink-0 items-center gap-1.5 rounded-lg px-2.5 py-2 text-sm text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)] md:inline-flex"
    >
      <PanelLeft size={17} />
      <span className="hidden lg:inline">{collapsed ? 'Show menu' : 'Hide menu'}</span>
    </button>
  );
}

export function SidebarShell() {
  const { collapsed, toggle } = useContext(SidebarContext);

  return (
    <aside
      data-collapsed={collapsed ? 'true' : undefined}
      className={`hidden shrink-0 flex-col border-r border-[var(--border)] bg-gradient-to-b from-[var(--surface)] via-[var(--surface)] to-brand/[0.04] transition-[width] duration-200 md:flex ${
        collapsed ? 'w-[68px] px-2 py-4' : 'w-64 p-4'
      }`}
    >
      <div
        className={`mb-6 flex items-center rounded-xl border border-[var(--border)] bg-[var(--surface)] ${
          collapsed ? 'justify-center p-1.5' : 'gap-2.5 p-2.5'
        }`}
        title={collapsed ? 'PioManage' : undefined}
      >
        <BrandMark size={collapsed ? 32 : 36} className="shrink-0 rounded-lg" />
        {!collapsed && (
          <div className="min-w-0">
            <div className="truncate text-[15px] font-semibold leading-tight tracking-tight">PioManage</div>
            <div className="truncate text-[10px] text-[var(--muted)]">Multi-tenant PSA Portal</div>
          </div>
        )}
      </div>

      <SidebarNav collapsed={collapsed} />

      {/* Storage is a figure, not an icon — there is nothing meaningful to show at 68px. */}
      {!collapsed && <StorageUsage />}

      {/* Kept alongside the header control: whichever one the eye lands on first, it works. */}
      <button
        type="button"
        onClick={toggle}
        aria-expanded={!collapsed}
        aria-label={collapsed ? 'Show menu labels' : 'Hide menu labels'}
        title={collapsed ? 'Show menu' : 'Hide menu'}
        className={`mt-2 flex items-center rounded-lg py-2 text-xs font-medium text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)] ${
          collapsed ? 'justify-center px-2' : 'gap-2 px-3'
        }`}
      >
        {collapsed ? <ChevronRight size={15} /> : <><ChevronLeft size={15} /> Hide menu</>}
      </button>
    </aside>
  );
}
