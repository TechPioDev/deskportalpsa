# Capacity exceptions (Phase 2)

A one-off change to when someone can take planned work. Two kinds:

| Kind | Meaning | Shapes |
|---|---|---|
| Unavailable | Not available for planned work during this time | Part of a day, or all day over a run of dates |
| Additional availability | Available outside the usual working hours | Part of a day only |

The reason (Meeting, Training, Appointment, Time off, Sick, Internal event, Other) is a **label for
planners**. There are no leave balances, accruals, entitlements, approval workflows or payroll
effects behind any of it. An optional note (plain text, 200 characters) is internal: it is never
part of anything a client receives.

## Part of a day

Date, start and end as wall-clock times in the person's time zone (their schedule's; the
organization's if they have no schedule). An end at or before the start runs into the next day.

Example: working 08:30–17:30, break 12:30–13:30, unavailable 15:00–16:00.

| | |
|---|---|
| Unavailable | − 1h |
| Usable | 7h |
| Free slots | 08:30–12:30, 13:30–15:00, 16:00–17:30 |

Only time that lands on working time is taken off: an unavailable period outside working hours
changes nothing, and one that overlaps a break does not take the break's time a second time.

## All day

First and last day (both included, up to a year). It takes off **the working window that starts on
each of those dates**, so a night shift that begins on a day off is off in full, including its
hours after midnight. This is the same rule the SLA clock uses for a holiday.

Result for each date: usable capacity 0, remaining 0, no free slots.

## Additional availability

Adds time **outside the working window**. It does not cancel a break (change the schedule for
that). Where it touches the window it makes one longer continuous slot (window to 17:30 plus extra
17:30–19:00 = one slot to 19:00). On a day the schedule does not work, it is all the capacity
there is.

It belongs to the shift date it starts on. Next to a night shift it never offers a minute twice:
extra availability 02:00–05:00 after a shift ending 03:00 adds only 03:00–05:00.

Where someone is both unavailable and additionally available, unavailable wins; a full day
unavailable cancels additional availability on that date.

## Rules

| Rule | Message |
|---|---|
| Start and end differ | "The start and end times are the same. For a whole day, choose "All day"." |
| All day is for unavailable only | "Extra availability needs a start and an end time." |
| Last day not before the first | "The last day is before the first." |
| At most 31 days back, a year ahead | "The date can be at most 31 days in the past." / "...at most a year ahead." |
| Note at most 200 characters, no control characters | "Keep the note to 200 characters." |
| Times that the clocks skip | "Those times do not exist on that date: the clocks go forward then." |
| The same exception twice | "That is already recorded for this person." |

Every problem is reported in one answer. Overlapping exceptions are allowed; they are merged when
capacity is worked out (see [capacity.md](capacity.md#interval-normalization)).

## Who can record them

`availability.manage`, by scope: see [permissions-and-security.md](permissions-and-security.md).
By default administrators and managers can; technicians cannot record their own. To let technicians
record their own time away, give the Technician role **Record time away and extra availability** at
scope **Own** in Roles & Permissions. No code change is needed.

## Audit

`workforce.exception.added`, `workforce.exception.updated` (before and after) and
`workforce.exception.removed`, each naming the person and describing the exception, for example
`Unavailable · 5 Oct 2026 15:00–16:00 (Asia/Kolkata) · Appointment`.

## Storage

Table `capacity_exceptions`: tenant, person, kind, all-day flag, first and last date, start and end
instants (part-day only), the zone it was entered in, reason, note, who created and last changed
it. Indexed by person and dates. It goes when the person does (cascade). The migration is additive.
