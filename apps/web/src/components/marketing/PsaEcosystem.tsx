import Link from 'next/link';
import { ArrowRight } from 'lucide-react';
import { PSA_PLATFORMS, PLATFORM_DESCRIPTOR, platformHref, type PsaPlatform } from '@/lib/psaPlatforms';

/**
 * One client experience across a PSA ecosystem — a hub with a spoke to each platform.
 *
 * Every spoke and every tile is identical by design. Differentiating them (weight, dash, colour,
 * a badge) would encode something about individual platforms that this page does not communicate,
 * so uniformity is the requirement rather than a stylistic preference.
 *
 * Marks are initials, not vendor logos: we have no licence to redistribute third-party brand
 * assets, and an approximated logo damages more trust than a clean monogram.
 */
export function PsaEcosystem() {
  const width = 1000;
  const hubX = width / 2;
  const slot = width / PSA_PLATFORMS.length;

  return (
    <>
      <svg
        viewBox="0 0 1000 300"
        className="hidden h-auto w-full lg:block"
        role="img"
        aria-label={`Desk Portal is built for ${PSA_PLATFORMS.length} PSA platforms: ${PSA_PLATFORMS.map((p) => p.name).join(', ')}.`}
      >
        {/* Two lines, not one. SVG text does not wrap, and as a single line this subtitle measured
            ~250 units inside a 260-unit box — touching both edges. The break is therefore explicit. */}
        <rect x={hubX - 140} y="12" width="280" height="86" rx="18" fill="var(--brand-line)" />
        <text x={hubX} y="42" textAnchor="middle" fontSize="16" fontWeight="600" fill="var(--surface)">
          Desk Portal
        </text>
        <text x={hubX} textAnchor="middle" fontSize="11" fill="var(--surface)" fillOpacity="0.8">
          <tspan x={hubX} y="63">One client experience</tspan>
          <tspan x={hubX} y="79">across your PSA ecosystem</tspan>
        </text>

        {PSA_PLATFORMS.map((p, i) => {
          const x = slot * i + slot / 2;
          const d = `M ${hubX} 98 C ${hubX} 158, ${x} 148, ${x} 198`;
          return (
            <g key={p.id}>
              <path d={d} fill="none" stroke="var(--brand-line)" strokeWidth="1.75" strokeOpacity="0.55" />
              <circle
                r="3.5"
                fill="var(--brand-line)"
                className="dp-travel"
                style={{ offsetPath: `path('${d}')`, animationDelay: `${-i * 0.4}s` }}
              />
              <rect x={x - 54} y="198" width="108" height="72" rx="14" fill="var(--surface)" stroke="var(--border)" />
              <circle cx={x} cy="222" r="13" fill="var(--brand-line)" fillOpacity="0.12" />
              <text x={x} y="227" textAnchor="middle" fontSize="10.5" fontWeight="700" fill="var(--brand-line)">
                {p.initials}
              </text>
              <text x={x} y="251" textAnchor="middle" fontSize="9.5" fontWeight="600" fill="var(--fg)">
                {p.name.length > 16 ? `${p.name.slice(0, 15)}…` : p.name}
              </text>
            </g>
          );
        })}
      </svg>

      <div className="lg:hidden">
        <div className="mx-auto mb-6 max-w-xs rounded-2xl bg-brand px-5 py-4 text-center text-brand-fg">
          <p className="text-sm font-semibold">Desk Portal</p>
          <p className="mt-0.5 text-[11.5px] leading-snug text-brand-fg/80">
            One client experience across your PSA ecosystem
          </p>
        </div>
        <PsaGrid />
      </div>
    </>
  );
}

function Tile({ p }: { p: PsaPlatform }) {
  return (
    <Link
      href={platformHref(p)}
      className="dp-lift group flex h-full items-center gap-3.5 rounded-2xl border border-[var(--border)] bg-[var(--surface)] p-5 transition-colors hover:border-brand/40"
    >
      <span
        className="flex h-11 w-11 shrink-0 items-center justify-center rounded-xl bg-brand text-[12.5px] font-bold tracking-wide text-brand-fg"
        aria-hidden="true"
      >
        {p.initials}
      </span>
      <span className="min-w-0 flex-1">
        <span className="block truncate text-[14px] font-semibold leading-tight">{p.name}</span>
        <span className="mt-1 block text-[12px] leading-snug text-[var(--muted)]">{PLATFORM_DESCRIPTOR}</span>
      </span>
      <ArrowRight
        size={15}
        aria-hidden="true"
        className="shrink-0 text-[var(--faint)] transition-all group-hover:translate-x-0.5 group-hover:text-brand"
      />
    </Link>
  );
}

/** Balanced 4 × 2 on desktop, 2 across on tablet, stacked on phones. */
export function PsaGrid() {
  return (
    <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
      {PSA_PLATFORMS.map((p) => <Tile key={p.id} p={p} />)}
    </div>
  );
}

/**
 * The compact coverage row for the home page — marks and names only.
 *
 * The full diagram and the descriptive cards live on the integrations page. Here the job is to
 * answer "does it cover mine?" in one glance and offer the way through.
 */
export function PsaStrip() {
  return (
    <ul className="flex flex-wrap items-center justify-center gap-2.5">
      {PSA_PLATFORMS.map((p) => (
        <li key={p.id}>
          <Link
            href={platformHref(p)}
            className="dp-lift flex items-center gap-2.5 rounded-xl border border-[var(--border)] bg-[var(--bg)] py-2.5 pl-2.5 pr-4 transition-colors hover:border-brand/40"
          >
            <span
              className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg bg-brand text-[11px] font-bold tracking-wide text-brand-fg"
              aria-hidden="true"
            >
              {p.initials}
            </span>
            <span className="text-[13.5px] font-medium">{p.name}</span>
          </Link>
        </li>
      ))}
    </ul>
  );
}
