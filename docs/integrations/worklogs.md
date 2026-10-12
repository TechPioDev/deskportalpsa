# Time entered in the PSA, kept as worklogs

Phase 9. A technician who writes their time down in the PSA itself has worked that time. Until
this slice the portal knew it only as a ticket's totals and an internal note: its time entries
were the ones logged from the portal and no others. Someone who worked a full day and logged it
in the PSA read, in the portal's figures, as having recorded nothing.

The owner decided on 6 October 2026 (decision 3 in section 20a of the
[audit](PHASE9_PSA_INTEGRATION_ARCHITECTURE_AUDIT.md)) that such time becomes worklogs: with its
source, its connection and the PSA's own id kept; one worklog however often it is delivered; the
technician, the time and the duration as they were; plainly marked as the PSA's; and never sent
back.

The code is `ProviderWorklogs` (the import), one call from `ConnectionSyncRunner`,
`PsaLoggedTime` (whose time it is) and a guard in `TicketTimeWriter.PushAsync`. The tests are
`ProviderWorklogTests` (on a SQL translator), `WorkforcePsaTimeTests`, and a volume test in
`ConnectionVolumeTests`.

## What a worklog is

A row of `ticket_time_entries`, the same table as time logged in the portal, with:

| | For an entry made in the PSA |
|---|---|
| `Source` | `Provider` (a portal entry is `Portal`). This is what marks it |
| `PsaConnectionId` | The PSA account it came from |
| `ExternalEntryId` | The PSA's own id for the entry |
| `TechnicianExternalId`, `TechnicianName` | The PSA login it is filed under, and that login's name as the PSA gives it |
| `Hours` | As the PSA holds them. Not rounded here |
| `EntryDate` | The moment the PSA says it was worked. Not the moment it was read |
| `Billable`, `WorkTypeLabel` | As the PSA has them: billable or not, and its word for the kind of work |
| `Notes` | What was written on the entry, the public and the internal text together. Staff only, like every time entry |
| `SyncStatus` | `Synced`: the PSA holds it, and there is nothing to send |
| `AppUserId` | Empty. Nobody logged it in the portal (see "Whose time it is") |

## One entry, one worklog

An entry is known by the PSA's id for it on its connection.

- **Read again, nothing is added.** Polling every five minutes, a "Sync now", a full re-sync and a
  run retried after a failure all read the same entries and find the worklogs already there.
- **The database refuses a second.** A unique index on the connection and the entry id
  (`IX_ticket_time_entries_PsaConnectionId_ExternalEntryId`) is the backstop should two reads of
  one ticket ever race. Production was checked before it was proposed: 17 entries, none sharing an
  id (read only, 7 October 2026).
- **An entry logged from the portal is not copied.** It was pushed to the PSA and comes back in
  every read of the ticket. It is recognised by the id the PSA gave it and left exactly as it is:
  who logged it, the reply it was logged with and the time on the clock are the portal's own
  record. An hour is counted once.
- **A webhook changes none of this.** Provider-native webhooks are not built yet (they need the
  owner's test environments). When they are, a delivered entry goes through this same
  reconciling, keyed the same way.

## What follows the PSA

The PSA is the record for an entry made in it.

- **Changed in the PSA** (the hours corrected, moved to another day, another login, re-worded): the
  worklog is rewritten to match, the next time the ticket is read.
- **Deleted in the PSA**: the worklog goes, the next time the ticket's time is read *successfully*.
  A read that failed removes nothing: a PSA that did not answer is not a ticket with no time.
- **An entry logged from the portal** is the portal's to account for. It is not rewritten from the
  PSA's copy and not removed because a read did not list it, as before this slice.

Worklogs are read when a ticket is read: in the same pass that already fetched the ticket's time
for its totals and time notes. It asks nothing more of the PSA. A ticket the PSA does not send
(because nothing on it changed) is not read, so an entry added to it shows when the PSA next
reports the ticket as changed, which adding time does. "Re-sync all" reads every ticket and
brings every worklog in at once; that is how the time already in the PSA arrives after this is
deployed.

## Never sent back

A worklog read from the PSA is the PSA's own entry. Sent to it, it would come back as a second
one, and that one would be read and sent again.

- It is stored as already in step, and the only things that send time look for entries that are
  not.
- `TicketTimeWriter.PushAsync`, which every send passes through, refuses one outright.
- The retry on the ticket answers "this entry is already recorded in the PSA".
- In the tests the stand-in PSA records every entry it is sent: after a sync, a second sync and a
  direct attempt, it has been sent none.

## Whose time it is

It is not stored on the worklog. An entry is filed under a PSA login, and it is the portal user's
that login is linked to **on that connection**, for as long as the link stands.

- Linking a login later credits the time already entered under it. Unlinking takes it away. No
  row is touched either way.
- The same login id on another PSA account is somebody else.
- The login the portal itself writes as (the connection's default time-entry resource) is nobody:
  it is the whole portal team's writing under one account. Linking a person to it gives them
  nothing.
- A login linked to nobody is nobody's here. Its time is on the ticket and in the reports that
  list PSA logins by their own names, and in no portal user's day.
- A worklog gives nobody a right to change the entry: that is still the login's owner or a board
  lead, with a reason when it is someone else's.

## Where it shows

| Screen | Before | Now |
|---|---|---|
| A ticket's time panel | Read live from the PSA, each entry marked "Portal → AT" or with the PSA's name | The same. The PSA's entries are marked with the PSA's name; the kept worklog is not mistaken for a portal entry |
| My day, the team's day | Time logged in the portal | And time entered in the PSA under the person's linked login |
| Workforce figures, forecast, trends, reports | Time logged in the portal; a note said PSA time was in no day | And PSA-entered time for linked people. In the list of recorded work it reads "Logged in the PSA". A note says how much of the figure it is |
| Dashboard, "my hours this week" | Time logged in the portal | And the person's PSA-entered time |
| Technician productivity, client workload, ticket filters by person | Already written to count time by PSA login, with only a few old rows to count | Now have the PSA's entries to count |
| Client review (QBR) hours | Summed from the portal's entries only | Summed from every entry the PSA holds for the client's tickets |
| A ticket's totals | The PSA's | Unchanged. The worklogs and the totals are the same hours, saved together |

Nothing about time reaches a client that did not before.

## What changes on the day it is deployed

Workforce figures for people with a linked PSA login go up by the time they entered in the PSA,
from the first sync that reads each ticket. In production today the two PSAs hold 119.72 hours
on 42 tickets (read 7 October 2026); how much of that is under a login linked to a portal user
was not counted. The definitions in
[PHASE7_ANALYTICS_METRIC_SPEC.md](../workforce-scheduling/PHASE7_ANALYTICS_METRIC_SPEC.md) are
updated to say so.

## Not in this slice

- **An edit made in the PSA to an entry that was logged from the portal** is not brought back to
  the portal's row (the ticket's totals do follow it). That was so before.
- **Time on a ticket the PSA has not reported as changed** arrives with the next read of that
  ticket, or with "Re-sync all".
- **Rounding.** A PSA that bills in increments holds the rounded figure; the worklog is that
  figure.
- **Proved against a real PSA.** The connectors' reading of time entries is held by the contract
  tests against stand-ins. That a real Autotask and a real ConnectWise deliver the same entry id
  on every read, and no duplicate on a retried page, is part of the certification that waits on
  the owner's test environments: BLOCKED — TEST ENVIRONMENT REQUIRED.

One migration, `ProviderWorklogs`: the unique index, and nothing else.
