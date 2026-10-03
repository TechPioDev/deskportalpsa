/**
 * The team scheduler's arithmetic: a time axis for a date in one zone, where a block sits on it,
 * where a pointer lands, and how overlapping blocks share a row. Pure functions over epoch
 * milliseconds and ISO instants, so overnight windows, DST and half-hour zones are handled by the
 * platform's zone tables rather than by assumptions about a day being 24 hours long.
 */

export type Instant = string;
const HOUR = 3_600_000;

const formatters = new Map<string, Intl.DateTimeFormat>();
/** One formatter per zone: building one is the expensive part, and a board asks thousands of times. */
function formatter(timeZone: string): Intl.DateTimeFormat {
  let f = formatters.get(timeZone);
  if (!f) {
    f = new Intl.DateTimeFormat('en-US', { timeZone, hourCycle: 'h23', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' });
    formatters.set(timeZone, f);
  }
  return f;
}

/** The clock parts of an instant as seen in a zone. */
export function wallParts(ms: number, timeZone: string): { year: number; month: number; day: number; hour: number; minute: number } {
  const parts = formatter(timeZone).formatToParts(new Date(ms));
  const get = (t: string) => Number(parts.find((p) => p.type === t)?.value ?? 0);
  return { year: get('year'), month: get('month'), day: get('day'), hour: get('hour'), minute: get('minute') };
}

function offsetMinutes(ms: number, timeZone: string): number {
  try {
    const w = wallParts(ms, timeZone);
    const asUtc = Date.UTC(w.year, w.month - 1, w.day, w.hour, w.minute, 0);
    return Math.round((asUtc - Math.floor(ms / 60_000) * 60_000) / 60_000);
  } catch { return 0; }
}

/**
 * "2026-10-05" + "14:00" in a zone, as epoch milliseconds. The browser has no "wall time in zone X"
 * primitive, so the offset is read back from the zone tables and corrected once; a time that does
 * not exist (spring forward) lands on the instant after the gap, an ambiguous one (fall back) on
 * its first occurrence.
 */
export function wallToMs(date: string, hm: string, timeZone: string): number {
  const [h, m] = hm.split(':').map(Number);
  const naive = Date.UTC(Number(date.slice(0, 4)), Number(date.slice(5, 7)) - 1, Number(date.slice(8, 10)), h, m);
  // Probe the offset on both sides of the asked moment, so a clock change either side is seen.
  const probes = [naive, naive - 3 * HOUR, naive + 3 * HOUR].map((at) => naive - offsetMinutes(at, timeZone) * 60_000);
  const reads = (ms: number) => { const w = wallParts(ms, timeZone); return w.hour === h && w.minute === m && w.day === Number(date.slice(8, 10)); };
  const valid = probes.filter(reads);
  // The earlier instant that reads as the asked time (the server's rule for an ambiguous start);
  // when none does (the skipped hour), the instant just after the gap.
  if (valid.length > 0) return Math.min(...valid);
  return Math.max(...probes.filter((p) => p >= naive - 24 * HOUR));
}

/** "2026-10-05" + "14:00" in a zone, as an ISO instant. */
export const wallToIso = (date: string, hm: string, timeZone: string) => new Date(wallToMs(date, hm, timeZone)).toISOString();

/** "HH:mm" of an instant in a zone. */
export function clock(ms: number, timeZone: string): string {
  const w = wallParts(ms, timeZone);
  return `${String(w.hour).padStart(2, '0')}:${String(w.minute).padStart(2, '0')}`;
}

/** "YYYY-MM-DD" of an instant in a zone. */
export function dateOf(ms: number, timeZone: string): string {
  const w = wallParts(ms, timeZone);
  return `${w.year}-${String(w.month).padStart(2, '0')}-${String(w.day).padStart(2, '0')}`;
}

export interface AxisInput {
  /** The person's working window on the day, if any. */
  windowStart?: Instant | null;
  windowEnd?: Instant | null;
  /** Anything else that must be on the axis: planned work, time away. */
  spans?: { start: Instant; end: Instant }[];
}

export interface Axis {
  /** Epoch ms of the left and right edges. */
  start: number;
  end: number;
  timeZone: string;
  /** One mark per hour of the axis, labelled in the zone; a fall-back day shows an hour twice, a spring-forward day skips one. */
  hours: { at: number; label: string }[];
}

/** The previous whole hour of the clock in the zone (not of UTC: half-hour zones exist). */
function floorHour(ms: number, timeZone: string): number {
  const w = wallParts(ms, timeZone);
  return ms - w.minute * 60_000 - (ms % 60_000);
}

/**
 * The axis for one date: from the earliest window start (or 08:00) to the latest window end (or
 * 18:00) across the rows, widened to whatever planned work or time away reaches beyond that, on
 * whole hours. An 18:00-03:00 night runs past midnight on the same axis, never cut at 24:00.
 */
export function buildAxis(date: string, timeZone: string, rows: AxisInput[], defaults: { start: string; end: string } = { start: '08:00', end: '18:00' }): Axis {
  let start = Number.POSITIVE_INFINITY;
  let end = Number.NEGATIVE_INFINITY;
  for (const r of rows) {
    if (r.windowStart && r.windowEnd) { start = Math.min(start, Date.parse(r.windowStart)); end = Math.max(end, Date.parse(r.windowEnd)); }
    for (const s of r.spans ?? []) { start = Math.min(start, Date.parse(s.start)); end = Math.max(end, Date.parse(s.end)); }
  }
  if (!Number.isFinite(start) || !Number.isFinite(end)) {
    start = wallToMs(date, defaults.start, timeZone);
    end = wallToMs(date, defaults.end, timeZone);
  }
  start = floorHour(start, timeZone);
  // Up to the next whole hour past the end, so the last block has a right edge to sit against.
  const endFloor = floorHour(end, timeZone);
  end = endFloor === end ? end : endFloor + HOUR;
  if (end - start < HOUR) end = start + HOUR;
  const hours: Axis['hours'] = [];
  for (let at = start; at < end; at += HOUR) hours.push({ at, label: clock(at, timeZone) });
  return { start, end, timeZone, hours };
}

/** Where a span sits on the axis, as percentages of its width, clipped to the axis. */
export function place(axis: Axis, start: Instant | number, end: Instant | number): { left: number; width: number; clippedStart: boolean; clippedEnd: boolean } {
  const s = typeof start === 'number' ? start : Date.parse(start);
  const e = typeof end === 'number' ? end : Date.parse(end);
  const span = axis.end - axis.start;
  const from = Math.max(axis.start, Math.min(axis.end, s));
  const to = Math.max(axis.start, Math.min(axis.end, e));
  return {
    left: ((from - axis.start) / span) * 100,
    width: Math.max(0, ((to - from) / span) * 100),
    clippedStart: s < axis.start,
    clippedEnd: e > axis.end,
  };
}

/** The instant under a pointer at `x` of `width` pixels across the axis, snapped to the nearest step. */
export function atPointer(axis: Axis, x: number, width: number, stepMinutes: number): number {
  const ratio = Math.max(0, Math.min(1, width > 0 ? x / width : 0));
  return snap(axis.start + ratio * (axis.end - axis.start), stepMinutes);
}

/** The nearest multiple of `stepMinutes` on the UTC clock, which is also the zone's clock for whole- and half-hour zones when the step divides 30. */
export function snap(ms: number, stepMinutes: number): number {
  const step = stepMinutes * 60_000;
  return Math.round(ms / step) * step;
}

export const minutesBetween = (start: Instant | number, end: Instant | number) =>
  Math.round(((typeof end === 'number' ? end : Date.parse(end)) - (typeof start === 'number' ? start : Date.parse(start))) / 60_000);

/**
 * Lanes for blocks that overlap in one row (an override placed over other work): each block takes
 * the first lane free at its start, so nothing is drawn on top of anything else.
 */
export function lanes<T extends { start: Instant; end: Instant }>(blocks: T[]): { block: T; lane: number; lanes: number }[] {
  const sorted = [...blocks].sort((a, b) => Date.parse(a.start) - Date.parse(b.start) || Date.parse(a.end) - Date.parse(b.end));
  const ends: number[] = [];
  const out: { block: T; lane: number; lanes: number }[] = [];
  for (const block of sorted) {
    const s = Date.parse(block.start);
    let lane = ends.findIndex((e) => e <= s);
    if (lane === -1) { lane = ends.length; ends.push(0); }
    ends[lane] = Date.parse(block.end);
    out.push({ block, lane, lanes: 0 });
  }
  for (const o of out) o.lanes = ends.length;
  return out;
}

/** "2h", "1h 30m", "45m". */
export function hoursLabel(minutes: number): string {
  if (minutes <= 0) return '0h';
  if (minutes < 60) return `${minutes}m`;
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  return m ? `${h}h ${String(m).padStart(2, '0')}m` : `${h}h`;
}
