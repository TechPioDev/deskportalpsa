import clsx from 'clsx';

const statusStyles: Record<string, string> = {
  NEW: 'bg-blue-100 text-blue-700 dark:bg-blue-950 dark:text-blue-300',
  IN_PROGRESS: 'bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300',
  WAITING_CUSTOMER: 'bg-purple-100 text-purple-700 dark:bg-purple-950 dark:text-purple-300',
  RESOLVED: 'bg-green-100 text-green-700 dark:bg-green-950 dark:text-green-300',
  CLOSED: 'bg-slate-200 text-slate-600 dark:bg-slate-800 dark:text-slate-300',
};

const priorityStyles: Record<string, string> = {
  LOW: 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300',
  NORMAL: 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300',
  HIGH: 'bg-orange-100 text-orange-700 dark:bg-orange-950 dark:text-orange-300',
  CRITICAL: 'bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300',
};

function Pill({ label, className }: { label: string; className: string }) {
  return (
    <span className={clsx('inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium', className)}>
      {label.replace(/_/g, ' ')}
    </span>
  );
}

export const StatusBadge = ({ status }: { status: string }) => (
  <Pill label={status} className={statusStyles[status] ?? statusStyles.NEW} />
);

export const PriorityBadge = ({ priority }: { priority: string }) => (
  <Pill label={priority} className={priorityStyles[priority.toUpperCase()] ?? priorityStyles.NORMAL} />
);

// ProviderType enum → short display name + tone. Extend as new connectors ship.
const providerMeta: Record<number, { name: string; abbr: string; className: string }> = {
  1: { name: 'ConnectWise', abbr: 'CW', className: 'bg-blue-100 text-blue-700 dark:bg-blue-950 dark:text-blue-300' },
  2: { name: 'Autotask', abbr: 'AT', className: 'bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300' },
  10: { name: 'HaloPSA', abbr: 'H', className: 'bg-sky-100 text-sky-700 dark:bg-sky-950 dark:text-sky-300' },
  20: { name: 'ServiceNow', abbr: 'SN', className: 'bg-slate-200 text-slate-700 dark:bg-slate-700 dark:text-slate-200' },
  21: { name: 'Freshservice', abbr: 'FS', className: 'bg-green-100 text-green-700 dark:bg-green-950 dark:text-green-300' },
  24: { name: 'Zendesk', abbr: 'ZD', className: 'bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300' },
};

/**
 * Which PSA (and which connection/tenant) a ticket came from — vital when an MSP runs several.
 * A ticket with no provider is the team's own work; it says so rather than inventing a PSA name.
 */
export function SourceBadge({ provider, connectionName }: { provider: string | number | null; connectionName?: string | null }) {
  if (provider === null || provider === undefined) {
    return (
      <span className="inline-flex items-center gap-1.5" title="Raised by the team, on an internal board">
        <span className="inline-flex h-5 min-w-5 items-center justify-center rounded bg-slate-200 px-1 text-[10px] font-bold text-slate-700 dark:bg-slate-700 dark:text-slate-200">IN</span>
        <span className="max-w-[10rem] truncate text-xs text-[var(--muted)]">{connectionName ?? 'Internal'}</span>
      </span>
    );
  }
  const meta = providerMeta[Number(provider)] ?? { name: `PSA ${provider}`, abbr: 'P', className: 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300' };
  return (
    <span className="inline-flex items-center gap-1.5" title={connectionName ? `${meta.name} · ${connectionName}` : meta.name}>
      <span className={clsx('inline-flex h-5 min-w-5 items-center justify-center rounded px-1 text-[10px] font-bold', meta.className)}>{meta.abbr}</span>
      <span className="max-w-[10rem] truncate text-xs text-[var(--muted)]">{connectionName ?? meta.name}</span>
    </span>
  );
}
