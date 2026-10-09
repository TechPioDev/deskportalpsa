/**
 * Pio, the PioManage owl — the single definition of the brand mark.
 *
 * The owl watches the desk and carries a ticket: the product's whole job in one shape. Drawn as
 * inline SVG rather than an image file so it inherits crispness at every size, costs no request,
 * and can be recoloured by variant instead of shipping a second asset.
 *
 * `primary` (cream owl on forest) is the mark: against the app's white surfaces a cream badge had
 * no presence, so the deep green carries it. `inverse` exists for dark or green surfaces where the
 * forest badge would disappear — same geometry, so the two are never out of step.
 */
type BrandVariant = 'primary' | 'inverse';

const PALETTE: Record<BrandVariant, {
  bg: string; body: string; wing: string; beak: string; pupil: string;
  ticket: string; ticketEdge: string; edge: string;
}> = {
  primary: {
    bg: '#14532D', body: '#FDF6E3', wing: '#86EFAC', beak: '#F97316', pupil: '#14532D',
    ticket: '#FDF6E3', ticketEdge: 'none', edge: 'none',
  },
  inverse: {
    bg: '#FDF6E3', body: '#14532D', wing: '#86EFAC', beak: '#EA580C', pupil: '#14532D',
    // Cream on a white page needs a hairline, or the badge has no edge at all.
    ticket: '#FFFFFF', ticketEdge: '#14532D', edge: '#14532D',
  },
};

export function BrandMark({
  size = 36,
  variant = 'primary',
  className,
  title = 'PioManage',
}: {
  size?: number;
  variant?: BrandVariant;
  className?: string;
  title?: string;
}) {
  const c = PALETTE[variant];
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 96 96"
      className={className}
      role="img"
      aria-label={title}
      focusable="false"
    >
      <rect width="96" height="96" rx="22" fill={c.bg} stroke={c.edge} strokeOpacity="0.18" />
      <path d="M28 34L34 17L45 31Z" fill={c.body} />
      <path d="M68 34L62 17L51 31Z" fill={c.body} />
      <ellipse cx="28" cy="54" rx="8" ry="15" fill={c.wing} />
      <ellipse cx="68" cy="54" rx="8" ry="15" fill={c.wing} />
      <ellipse cx="48" cy="50" rx="24" ry="24" fill={c.body} />
      <circle cx="37" cy="44" r="10" fill={c.wing} />
      <circle cx="59" cy="44" r="10" fill={c.wing} />
      <circle cx="37" cy="44" r="5" fill={c.pupil} />
      <circle cx="59" cy="44" r="5" fill={c.pupil} />
      <path d="M48 54L43 62L53 62Z" fill={c.beak} />
      <path d="M42 68H46V74H42Z" fill={c.beak} />
      <path d="M50 68H54V74H50Z" fill={c.beak} />
      <path
        d="M37 72H59A3 3 0 0 1 62 75V82A3 3 0 0 1 59 85H37A3 3 0 0 1 34 82V75A3 3 0 0 1 37 72Z"
        fill={c.ticket}
        stroke={c.ticketEdge}
        strokeWidth="1.5"
      />
      <path d="M54 74V83" stroke={c.pupil} strokeWidth="2" strokeDasharray="3 3" />
    </svg>
  );
}
