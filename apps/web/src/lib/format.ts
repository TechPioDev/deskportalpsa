/**
 * Hours as the desk reads them: whole hours from ten up, one decimal below.
 *
 * Shared, not copied, because figures link across pages: a client's hours on Client workload open
 * that client's ticket list, which totals the same hours. Two formatters would print one number as
 * "2.0h" there and "2h" here, and a reader checking a link fairly asks which one is wrong.
 */
export const fmtHours = (h: number) => (h >= 10 ? `${Math.round(h)}h` : `${h.toFixed(1)}h`);
