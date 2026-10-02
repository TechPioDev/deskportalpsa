# Capacity, availability and free time (Phase 2)

How much of a person's time is offered for planned work on a date, what is left of it, and exactly
when they are free.

This is **work capacity planning**. It is not attendance, clock-in/out, payroll or leave management,
and it does not measure how anyone performs: capacity, scheduled utilization, actual utilization,
productivity and quality are different things, and Phase 2 computes only the first. There are no
scores and no rankings.

Internal only: see [permissions-and-security.md](permissions-and-security.md).

## Terms

| Term | Meaning |
|---|---|
| Working window | The configured working period for a day (Phase 1). Never called attendance. |
| Break | A planned non-work period inside the working window. |
| Capacity exception | A one-off change: time unavailable, or additional availability. See [availability-exceptions.md](availability-exceptions.md). |
| Usable capacity | Working time left after breaks and exceptions. |
| Allocated time | Work already placed in the person's time: **confirmed** or **tentative**. |
| Remaining capacity | Usable capacity less confirmed work. |
| Free slot | One exact, continuous stretch in which more work can fit. |
| Actual time | Time logged against work. Not used by Phase 2. |

A date is a **shift date**: the day the working window *starts* on, in the person's own time zone. A
night shift 18:00–03:00 on Monday is Monday's capacity, including its hours after midnight.

## The formula

```
Gross working window
  − breaks
  − unavailable exceptions
  + additional availability
  = USABLE CAPACITY

Usable capacity − confirmed work  = REMAINING (confirmed) capacity   → the free slots
Remaining       − tentative work  = PROJECTED remaining capacity     → the projected free slots
```

Each figure is **measured from the real time left after the step**, not by subtracting durations.
That is what makes these always true (and tested on every case):

- `Usable = Gross − Breaks − Unavailable + Additional`
- `Remaining = Usable − Confirmed`, and it equals the total length of the free slots
- `Projected = Remaining − Tentative`
- no figure is ever negative

**Confirmed and tentative work are never mixed.** Tentative (pencilled-in) work holds no capacity:
its time stays a free slot until it is confirmed. It is reported as its own figure, and as what
would be left if it were confirmed.

### Worked example

| | |
|---|---|
| Working window | 08:30–17:30 = 9h |
| Break | 12:30–13:30 = 1h |
| **Usable capacity** | **8h** |
| Confirmed work | 5h |
| Tentative work | 1h |
| **Remaining (confirmed)** | **3h** |
| **Projected remaining** | **2h** |

## Interval normalization

All arithmetic is done on sets of real time (`Intervals`). Every set is **normalized**: sorted, with
overlapping stretches merged into one, and stretches that touch merged too.

| Input | Counted as | Not as |
|---|---|---|
| Unavailable 10:00–11:30 and 11:00–12:00 | 10:00–12:00 = 2h | 2h 30m |
| Unavailable 10:00–11:00 and 11:00–12:00 (touching) | one period 10:00–12:00 | two periods with a gap |
| Unavailable 12:00–14:00 over a 12:30–13:30 break | 1h of working time | 2h (the break hour twice) |
| Two confirmed pieces that overlap each other | that time once | twice |

Stretches are **half-open**: work ending at 10:00 and work starting at 10:00 do not overlap.

## Free-slot algorithm

For a person and a shift date, deterministically:

1. Take the working window as real instants (Phase 1: zone, overnight, clock changes).
2. Subtract breaks.
3. Subtract unavailable exceptions. A full-day exception removes everything and cancels any
   additional availability on that date.
4. Add additional availability, except where the person is also unavailable.
5. Subtract confirmed allocations → **free slots**.
6. Subtract tentative allocations from those → **projected free slots**.
7. Normalize.

Example: working 08:30–17:30, break 12:30–13:30, scheduled 08:30–09:30, 10:30–11:30, 13:30–15:00.

| Free slot | Length |
|---|---|
| 09:30–10:30 | 1h |
| 11:30–12:30 | 1h |
| 15:00–17:30 | 2h 30m |
| **Total remaining** | **4h 30m** |

Work planned outside the offered time takes no capacity (it is overtime, reported in a later phase).

## Duration search

"Can this person fit 90 minutes today?" needs **one continuous slot**. With the slots above:
90 minutes fits at **15:00–16:30** (the earliest slot long enough); two separate free hours are not a
two-hour slot, so 151 minutes fits nowhere even though 270 are free.

## Find available technician

`GET api/workforce/availability` answers "who has N continuous minutes?" with facts, earliest first.

| Input | Meaning |
|---|---|
| `from`, `to` | Dates to search (at most 14). Each person is returned for the **first** date they fit. |
| `duration` | Minutes of continuous work (5 minutes to 12 hours). |
| `earliest`, `latest` | Time of day, in `timeZone` (the organization's by default). A latest time at or before the earliest runs into the next day. |
| `teamId`, `departmentId`, `people` | Narrow who is looked at. Existing teams and departments; nothing is duplicated. |
| `skills`, `matchAll` | Phase 1 skills. **`matchAll=true` (the default) means the person must hold every skill; `false` means any one.** The answer repeats which rule was used. Levels are returned as facts; they do not filter. |

The answer lists, per person: the date, the recommended slot (the earliest stretch of exactly the
length asked for), every window that day long enough, the free minutes inside the searched window,
and the matching skills. It also says how many people were looked at and why the rest were left out
(without the skills, not offered for planned work, no working schedule, no slot long enough).

There is no scoring and no AI recommendation. Nothing is offered in the past: a search for today
starts from the next whole minute. Nothing is booked or reserved by a search.

## Time zones

- A working window is wall-clock time in the schedule's IANA zone; everything is computed and
  returned as UTC instants, each with the zone to show it in.
- A capacity date is the person's shift date in their own zone. The team view uses the same
  calendar date for everyone, read in each person's zone.
- The search window is read in the organization's zone (or the one given) and compared with every
  person's free time as instants, so a technician in another zone is matched correctly.
- The screens always format a time in a named zone (the technician's, or the search's), never the
  browser's by accident.

Tested with `Asia/Kolkata`, `America/New_York` and `Europe/London`.

## Overnight work

Working 18:00–03:00, break 22:00–22:30, scheduled 23:00–01:00:

| | |
|---|---|
| Gross | 9h |
| Break | 30m |
| Usable | 8h 30m |
| Confirmed | 2h |
| Remaining | 6h 30m |
| Free slots | 18:00–22:00, 22:30–23:00, 01:00–03:00 (next calendar day) |

An appointment at 01:00 on Tuesday comes off **Monday's** night shift. A search for Tuesday
01:00–04:00 finds the free hours of the shift that began on Monday.

## Clock changes (DST)

| Case | Behaviour |
|---|---|
| Spring-forward night (22:00–06:00) | Really 7 hours: 7h gross. No negative or missing time. |
| Fall-back night (22:00–06:00) | Really 9 hours: 9h gross, counted once. |
| A time the clocks skip (02:15) | Moves forward by the gap (03:15). |
| A time the clocks repeat (01:30) | A start takes the first occurrence, an end the second, so a window keeps its true length. |
| A break or a short exception inside the repeated hour (01:15–01:45) | Keeps its own length (30 minutes). |

The last line fixes a Phase 1 defect found in this phase: such a break was read as 90 minutes.

## Performance

Everything for a request is read in a fixed number of queries, whatever the number of people or
days: schedules, exceptions, planned work, holidays, then teams and skills. Nothing is queried per
person or per day, and nothing is cached (availability changes with every schedule, exception and
booking, so a cache would have to be invalidated by all three; the engine is fast enough without).

Measured on **PostgreSQL 17** (a container on a development machine, the schema built by the real
migrations; `CapacityPerformanceTests` with `DESK_TEST_POSTGRES` set), with two schedule versions
and three pieces of planned work per person per day:

| People | Team capacity, 1 day | Search, 1 day | Search, 7 days | Search, 14 days | One person, 30 days | Engine, everyone × 30 days |
|---|---|---|---|---|---|---|
| 50 | 12 queries, 95 ms | 9, 158 ms | 10, 64 ms | 9, 159 ms | 10, 56 ms | 3, 193 ms |
| 100 | 12 queries, 110 ms | 9, 73 ms | 10, 48 ms | 9, 91 ms | 10, 14 ms | 3, 116 ms |
| 500 | 12 queries, 261 ms | 9, 299 ms | 10, 99 ms | 9, 447 ms | 10, 20 ms | 3, 590 ms |

(The 50-person row ran first and carries the one-off warm-up.) The same test runs on SQLite in every
CI run and **asserts the query counts**, so a query per person or per day would fail the build.
"Engine, everyone × 30 days" is the calculation alone for all of them over a month, further than
any one request may ask.

Limits that bound the cost of any one request: 31 days of capacity, 14 days of search, 1,000 people
(narrow by team beyond that), 200 matches returned, 20 skills, work of at most 12 hours (search) or
24 hours (conflict check).

A load test against the production server is part of the hardening phase.

## Where planned work comes from

The engine does not know any ticket type. It asks `IWorkAllocationReader` for the work already
placed in people's time, so the same capacity works for the team's own tickets, Autotask and
ConnectWise tickets, and later projects or monitoring work. No ticket entity is duplicated.

**In Phase 2 nothing can be booked yet**, so the registered reader returns no work and confirmed
and tentative figures are 0. The engine is tested against real allocations through that seam; work
allocation (the next phase) replaces the one registration.

## Holidays

A desk holiday (the calendar the SLA clock uses) is **shown** on the day and takes no capacity by
itself: a desk that works its holidays keeps its capacity. Record time away for whoever is off.
