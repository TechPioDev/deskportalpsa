import { test, expect } from '@playwright/test';
import { atPointer, buildAxis, clock, dateOf, hoursLabel, lanes, minutesBetween, place, snap, wallToIso, wallToMs } from './timeline';

/**
 * The scheduler's arithmetic, run in Node with full zone tables: an ordinary day, a night shift
 * across midnight, a half-hour zone, and both clock changes. No browser, no server.
 */

test('wall times in a zone become the right instants, half-hour zones included', () => {
  expect(wallToIso('2026-10-05', '14:00', 'UTC')).toBe('2026-10-05T14:00:00.000Z');
  expect(wallToIso('2026-10-05', '14:00', 'Europe/London')).toBe('2026-10-05T13:00:00.000Z');
  expect(wallToIso('2026-10-05', '09:30', 'Asia/Kolkata')).toBe('2026-10-05T04:00:00.000Z');
  expect(wallToIso('2026-10-05', '09:00', 'America/New_York')).toBe('2026-10-05T13:00:00.000Z');
  expect(clock(wallToMs('2026-10-05', '09:30', 'Asia/Kolkata'), 'Asia/Kolkata')).toBe('09:30');
  expect(dateOf(wallToMs('2026-10-05', '00:15', 'Asia/Kolkata'), 'UTC')).toBe('2026-10-04');
});

test('a normal day: the axis spans the working window on whole hours and blocks sit where they should', () => {
  const axis = buildAxis('2026-10-05', 'UTC', [
    { windowStart: '2026-10-05T08:30:00Z', windowEnd: '2026-10-05T17:30:00Z' },
    { windowStart: '2026-10-05T09:00:00Z', windowEnd: '2026-10-05T18:00:00Z' },
  ]);
  expect(clock(axis.start, 'UTC')).toBe('08:00');
  expect(clock(axis.end, 'UTC')).toBe('18:00');
  expect(axis.hours.map((h) => h.label)).toEqual(['08:00', '09:00', '10:00', '11:00', '12:00', '13:00', '14:00', '15:00', '16:00', '17:00']);
  const p = place(axis, '2026-10-05T10:00:00Z', '2026-10-05T11:00:00Z');
  expect(p.left).toBeCloseTo(20, 5);
  expect(p.width).toBeCloseTo(10, 5);
  expect(p.clippedStart).toBe(false);
  // Work reaching beyond the window widens the axis rather than being cut.
  const wider = buildAxis('2026-10-05', 'UTC', [{ windowStart: '2026-10-05T08:30:00Z', windowEnd: '2026-10-05T17:30:00Z', spans: [{ start: '2026-10-05T18:30:00Z', end: '2026-10-05T19:15:00Z' }] }]);
  expect(clock(wider.end, 'UTC')).toBe('20:00');
  // Nothing at all: 08:00-18:00.
  const empty = buildAxis('2026-10-05', 'Europe/London', []);
  expect([clock(empty.start, 'Europe/London'), clock(empty.end, 'Europe/London')]).toEqual(['08:00', '18:00']);
});

test('a night shift runs past midnight on one axis', () => {
  const axis = buildAxis('2026-10-05', 'UTC', [
    { windowStart: '2026-10-05T08:30:00Z', windowEnd: '2026-10-05T17:30:00Z' },
    { windowStart: '2026-10-05T18:00:00Z', windowEnd: '2026-10-06T03:00:00Z' },
  ]);
  expect(axis.hours).toHaveLength(19);
  expect(axis.hours[axis.hours.length - 1].label).toBe('02:00');
  expect(dateOf(axis.end, 'UTC')).toBe('2026-10-06');
  const late = place(axis, '2026-10-06T01:00:00Z', '2026-10-06T02:30:00Z');
  expect(late.left).toBeCloseTo((17 / 19) * 100, 5);
  expect(late.width).toBeCloseTo((1.5 / 19) * 100, 5);
});

test('fall back: the repeated hour is on the axis twice, and nothing has a negative length', () => {
  // America/New_York, 1 Nov 2026: 01:00-02:00 EDT then 01:00-02:00 EST.
  const axis = buildAxis('2026-11-01', 'America/New_York', [{ windowStart: '2026-11-01T04:00:00Z', windowEnd: '2026-11-01T10:00:00Z' }]);
  expect(axis.hours.map((h) => h.label)).toEqual(['00:00', '01:00', '01:00', '02:00', '03:00', '04:00']);
  expect(axis.end - axis.start).toBe(6 * 3_600_000);
  const across = place(axis, '2026-11-01T05:30:00Z', '2026-11-01T06:30:00Z');
  expect(across.width).toBeCloseTo((1 / 6) * 100, 5);
  expect(minutesBetween('2026-11-01T05:30:00Z', '2026-11-01T06:30:00Z')).toBe(60);
  // The ambiguous wall time resolves to its first occurrence, west or east of UTC.
  expect(wallToIso('2026-11-01', '01:30', 'America/New_York')).toBe('2026-11-01T05:30:00.000Z');
  // Europe/Berlin, 25 Oct 2026: 02:30 happens twice (CEST, then CET); the first is 00:30Z.
  expect(wallToIso('2026-10-25', '02:30', 'Europe/Berlin')).toBe('2026-10-25T00:30:00.000Z');
  expect(wallToIso('2026-10-25', '04:00', 'Europe/Berlin')).toBe('2026-10-25T03:00:00.000Z');
});

test('spring forward: the skipped hour is not on the axis and a block across the gap keeps its real length', () => {
  // Europe/London, 29 Mar 2026: 01:00 GMT becomes 02:00 BST.
  const axis = buildAxis('2026-03-29', 'Europe/London', [{ windowStart: '2026-03-29T00:00:00Z', windowEnd: '2026-03-29T04:00:00Z' }]);
  expect(axis.hours.map((h) => h.label)).toEqual(['00:00', '02:00', '03:00', '04:00']);
  const across = place(axis, '2026-03-29T00:30:00Z', '2026-03-29T01:30:00Z');
  expect(across.width).toBeCloseTo(25, 5);
  // A time that never happens lands after the gap rather than nowhere.
  expect(wallToIso('2026-03-29', '01:30', 'Europe/London')).toBe('2026-03-29T01:30:00.000Z');
  expect(clock(Date.parse('2026-03-29T01:30:00Z'), 'Europe/London')).toBe('02:30');
});

test('pointer positions snap to the step and invert the placement', () => {
  const axis = buildAxis('2026-10-05', 'UTC', [{ windowStart: '2026-10-05T08:00:00Z', windowEnd: '2026-10-05T18:00:00Z' }]);
  expect(clock(atPointer(axis, 250, 1000, 15), 'UTC')).toBe('10:30');
  expect(clock(atPointer(axis, 262, 1000, 15), 'UTC')).toBe('10:30');
  expect(clock(atPointer(axis, 275, 1000, 15), 'UTC')).toBe('10:45');
  expect(clock(atPointer(axis, -40, 1000, 15), 'UTC')).toBe('08:00');
  expect(clock(atPointer(axis, 5000, 1000, 15), 'UTC')).toBe('18:00');
  expect(clock(snap(Date.parse('2026-10-05T10:07:00Z'), 15), 'UTC')).toBe('10:00');
  expect(clock(snap(Date.parse('2026-10-05T10:08:00Z'), 15), 'UTC')).toBe('10:15');
});

test('overlapping blocks take separate lanes; blocks that only touch share one', () => {
  const laid = lanes([
    { id: 'a', start: '2026-10-05T09:00:00Z', end: '2026-10-05T11:00:00Z' },
    { id: 'b', start: '2026-10-05T10:00:00Z', end: '2026-10-05T12:00:00Z' },
    { id: 'c', start: '2026-10-05T11:00:00Z', end: '2026-10-05T13:00:00Z' },
    { id: 'd', start: '2026-10-05T14:00:00Z', end: '2026-10-05T15:00:00Z' },
  ]);
  const byId = Object.fromEntries(laid.map((l) => [l.block.id, l.lane]));
  expect(byId).toEqual({ a: 0, b: 1, c: 0, d: 0 });
  expect(laid[0].lanes).toBe(2);
  expect(lanes([]).length).toBe(0);
});

test('durations read naturally', () => {
  expect(hoursLabel(0)).toBe('0h');
  expect(hoursLabel(45)).toBe('45m');
  expect(hoursLabel(60)).toBe('1h');
  expect(hoursLabel(90)).toBe('1h 30m');
  expect(hoursLabel(150)).toBe('2h 30m');
});
