# Conflict evaluation (Phase 2)

Whether a piece of work fits in someone's time at a proposed moment, and exactly what is in the way
if it does not. `GET api/workforce/people/{id}/conflicts?start&end&tentative&skills`.

Phase 2 provides the evaluation. Phase 3 uses it to place work: every write to a plan runs it again
under the person's gate, and a refusal is a 409 with the conflicts in its payload
([planned-work.md](planned-work.md#conflicts-and-overrides)).

## Conflict types

| Type | When | Confirmed proposal | Tentative proposal |
|---|---|---|---|
| `NotSchedulable` (8) | The person is not offered for planned work, or their account is inactive | **Block** | **Block** |
| `UnavailableConflict` (4) | Overlaps an unavailable exception, or the working window of a day taken off in full | **Block** | **Block** |
| `HardConflict` (1) | Overlaps existing confirmed work | **Overridable** | Warning |
| `OutsideWorkingWindow` (5) | Falls outside the working window and any additional availability | **Overridable** | Warning |
| `OverCapacity` (6) | Longer than all the capacity left on the day(s) it touches | **Overridable** | Warning |
| `BreakConflict` (3) | Overlaps a planned break | **Overridable** (a Warning in Phase 2; changed in Phase 3, see below) | Warning |
| `TentativeConflict` (2) | Overlaps tentative work | Warning | Warning |
| `SkillWarning` (7) | A requested skill is not one the person holds | Warning | Warning |

Severity: 1 Warning, 2 Overridable, 3 Block.

Why they are not all overridable:

- **Block** means "change the cause instead". Booking someone while the record says they are away
  would make the record false; the exception is changed or removed first. The same goes for
  someone switched out of planned work.
- **Overridable** means it needs a deliberate decision by someone allowed to make it, with a
  reason: a double booking, overtime, more than the day holds.
- **Warning** is worth knowing and stops nothing: tentative work holds no capacity, and skills
  guide a choice rather than forbid it.
- **Breaks** became overridable in Phase 3, when work could actually be placed: time over a break is
  time the break does not offer, so the planned duration and the capacity taken would silently
  disagree. Working through lunch is a decision with a reason, like overtime.
- A **tentative** proposal holds no capacity itself, so what would be overridable for confirmed
  work is only a warning for it. Blocks stay blocks.

## The answer

```json
{
  "canSchedule": false,
  "canOverride": true,
  "conflicts": [
    {
      "type": 1,
      "severity": 2,
      "start": "2026-10-05T10:30:00+00:00",
      "end": "2026-10-05T11:00:00+00:00",
      "message": "Already has confirmed work during this period.",
      "blockingWorkId": "…"
    }
  ]
}
```

- `canSchedule`: nothing blocks and nothing needs an override.
- `canOverride`: nothing blocks, and at least one conflict needs an override.
- Every conflict is reported at once, each with the exact overlapping stretch.
- `OverCapacity` and `HardConflict` are separate: work that fits the day but clashes at that hour is
  a hard conflict only.

## What a conflict does not reveal

A conflict never carries a ticket's title, client or number. `blockingWorkId` is present only for
work the caller may see; for any other work it is null and the message is the same generic
sentence, so the caller learns the time is taken and nothing else. This matters across teams and
permissions.

## Availability is not a reservation

Manager A sees 15:00–16:00 free. Manager B sees the same. Both try to book it.

The evaluation is pure and reads everything fresh on every call (nothing is cached), so it can be
called again **inside the transaction that books the work**. That transaction must re-check and
refuse the second booking; it must never rely on what a screen showed earlier. This is tested:
after A books, B's check on the same slot returns `canSchedule: false`.

Phase 3 does exactly this: `WorkPlanService` takes the person's `PlanningGate` (a row lock on
PostgreSQL) and evaluates inside it, so of two simultaneous bookings exactly one wins and the other
is told "This time is no longer available" ([planned-work.md](planned-work.md#concurrency)). When
work is moved, its own current place is ignored (`ProposedWork.IgnoreAllocationId`).

## Limits

The proposal must end after it starts, be at most 24 hours long, and start between a month ago and
a year ahead. Placing work is stricter: it must start no earlier than yesterday
([planned-work.md](planned-work.md#validation)).
