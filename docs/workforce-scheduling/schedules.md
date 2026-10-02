# Working schedules

A schedule says when someone normally works, so work can later be planned against real capacity.
It is a **capacity boundary, never attendance**.

## Shape

- **Weekdays:** each weekday is either not working or has one working window (start to end) and up
  to 4 planned breaks.
- **Overnight windows:** an end at or before the start means the window runs past midnight. For
  example, 18:00 to 03:00 is a 9-hour window that **belongs to the day it starts on**. Start and end
  can't be equal: a zero-length day is a day off, and 24 hours is not a shift.
- **Breaks** must sit wholly inside the window, on the window's own timeline. In an 18:00-03:00 night,
  00:00-00:30 is inside, but 17:00-18:30 and 03:00-04:00 are not. Breaks can't overlap, and they can't
  remove all the working time.
- **Figures shown:**
  - **Gross** = window length
  - **Breaks** = sum of breaks
  - **To work** (usable) = gross minus breaks

  Example: 08:30-17:30 with a 12:30-13:30 break is 9h gross, 1h of breaks and 8h to work.

## Versions

A change starts on a chosen day (today or later, at most a year ahead). Earlier days keep the
schedule they had, so capacity figures for the past never move. Saving with the same start day
corrects that version. A change that hasn't started yet can be withdrawn.

## Time zones

| Where | What |
|---|---|
| Storage | Instants in UTC (unchanged PIO convention). Schedules store **wall-clock times** plus the person's **IANA zone id** (Windows ids are converted). |
| Organization zone | Default for a new schedule (`MspOrganization.TimeZone`). |
| Person's zone | Per schedule version, so someone moving region is a new version. |
| Browser | Shows the zone list from `Intl`; never converts the schedule's wall times. |

When a working day becomes real instants (`WorkingWindow.Instants`, which capacity uses from Phase 2):
- **A time the clock skips** (spring forward, e.g. 02:30) moves forward by the gap, to 03:30.
- **A time the clock passes twice** (fall back, e.g. 01:30): a start takes the **first** occurrence
  and an end the **second**, so the window keeps its true elapsed length.
- **A break keeps its own length** whatever the clocks do: it starts at its wall-clock time and
  lasts as long as it is set to. (Until Phase 2 a 01:15-01:45 break on a fall-back night was read
  as ninety minutes, the first 01:15 to the second 01:45. Fixed.)
- **Effect on night shifts:** a night spanning spring-forward has an hour less to work, and one
  spanning fall-back has an hour more.

Saving a schedule again for the day it starts on **corrects** that version rather than adding one.
(On a real database this answered a server error until Phase 2; fixed, with a test on a real SQL
engine.)

Development note: the solution runs in invariant-globalization mode. On **Windows** that removes
IANA zone data, so a dev machine accepts well-formed IANA ids without checking them and resolves
them to UTC. The Linux servers (production, CI) check and resolve every id properly.
