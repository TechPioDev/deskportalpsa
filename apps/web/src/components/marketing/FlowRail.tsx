import { User, LayoutDashboard, Database, Wrench, ArrowDown } from 'lucide-react';

/**
 * Client → PioManage → PSA → Technician, with packets travelling the wire.
 *
 * Desktop draws it as one SVG so the rail, the nodes and the packets scale as a single unit —
 * `offset-path` follows the drawn curve exactly, so a packet can never drift off the line it is
 * meant to be on. Below md that geometry would be unreadable, so the same story is told as a
 * stacked list rather than a shrunken diagram nobody can parse.
 */

const NODES = [
  { x: 95, label: 'Client', sub: 'Submits a request' },
  { x: 365, label: 'PioManage', sub: 'Client experience' },
  { x: 635, label: 'Your PSA', sub: 'System of record' },
  { x: 905, label: 'Technician', sub: 'Works where they always have' },
];

const RAIL = 'M 172 118 H 828';

const PACKETS = [
  { label: 'New ticket', delay: '0s', dir: 1 },
  { label: 'Reply', delay: '-1.6s', dir: -1 },
  { label: 'Attachment', delay: '-3.2s', dir: 1 },
];

const MOBILE = [
  { icon: User, label: 'Client', sub: 'Submits a request, adds a screenshot, follows progress.' },
  { icon: LayoutDashboard, label: 'PioManage', sub: 'Receives it, shows status, keeps the conversation in one place.' },
  { icon: Database, label: 'Your PSA', sub: 'The ticket is created and updated. Your PSA stays the system of record.' },
  { icon: Wrench, label: 'Technician', sub: 'Replies from the PSA. The answer appears back in the portal.' },
];

export function FlowRail() {
  return (
    <>
      <svg
        viewBox="0 0 1000 236"
        className="hidden h-auto w-full md:block"
        role="img"
        aria-label="A request travels from the client into PioManage, on to whichever PSA the provider runs, and to the technician — with replies, attachments and status flowing back the same way."
      >
        <defs>
          <linearGradient id="railGrad" x1="0" x2="1">
            <stop offset="0%" stopColor="var(--brand-line)" stopOpacity="0.25" />
            <stop offset="50%" stopColor="var(--brand-line)" stopOpacity="0.9" />
            <stop offset="100%" stopColor="var(--brand-line)" stopOpacity="0.25" />
          </linearGradient>
        </defs>

        <path d={RAIL} fill="none" stroke="url(#railGrad)" strokeWidth="2" />
        <path d={RAIL} fill="none" stroke="var(--brand-line)" strokeWidth="2" strokeOpacity="0.5" className="dp-flow" />

        {PACKETS.map((p) => (
          <g
            key={p.label}
            className="dp-travel"
            style={{
              offsetPath: `path('${RAIL}')`,
              animationDelay: p.delay,
              animationDirection: p.dir === -1 ? 'reverse' : 'normal',
            }}
          >
            <rect x="-38" y="-13" width="76" height="26" rx="13" fill="var(--surface)" stroke="var(--brand-line)" strokeWidth="1.5" />
            <text x="0" y="4" textAnchor="middle" fontSize="11" fontWeight="600" fill="var(--brand-line)">
              {p.label}
            </text>
          </g>
        ))}

        {NODES.map((n, i) => (
          <g key={n.label}>
            <rect
              x={n.x - 78} y={62} width="156" height="112" rx="16"
              fill="var(--surface)" stroke="var(--border)"
            />
            <circle cx={n.x} cy={92} r="15" fill="var(--brand-line)" fillOpacity="0.12" />
            <circle cx={n.x} cy={92} r="4.5" fill="var(--brand-line)" />
            <text x={n.x} y={131} textAnchor="middle" fontSize="12.5" fontWeight="600" fill="var(--fg)">
              {n.label}
            </text>
            <text x={n.x} y={150} textAnchor="middle" fontSize="10.5" fill="var(--muted)">
              {n.sub.length > 26 ? `${n.sub.slice(0, 24)}…` : n.sub}
            </text>
            <text x={n.x} y={200} textAnchor="middle" fontSize="10.5" fill="var(--faint)">
              {['01', '02', '03', '04'][i]}
            </text>
          </g>
        ))}
      </svg>

      <ol className="space-y-3 md:hidden">
        {MOBILE.map(({ icon: Icon, label, sub }, i) => (
          <li key={label}>
            <div className="flex gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4">
              <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-brand-tint text-brand-deep dark:bg-brand/25 dark:text-brand-soft">
                <Icon size={17} aria-hidden="true" />
              </span>
              <span>
                <span className="block text-sm font-semibold">{label}</span>
                <span className="mt-1 block text-[13px] leading-relaxed text-[var(--muted)]">{sub}</span>
              </span>
            </div>
            {i < MOBILE.length - 1 && (
              <ArrowDown size={15} aria-hidden="true" className="mx-auto my-1.5 text-brand-mid" />
            )}
          </li>
        ))}
      </ol>
    </>
  );
}
