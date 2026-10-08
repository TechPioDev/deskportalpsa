# Extending a ticket's due date

The owner asked for it on 8 October 2026 and decided the two open points the same day: the new
date is **written to the PSA** for a PSA ticket, and **anyone with `tickets.update`** may do it.

## What happens

On a ticket that is not finished, staff who may change it see **Extend due date** under the
details. They choose a later date and say why (a few words at least, 500 characters at most).

- **PSA ticket:** the date is sent to the PSA first (Autotask `dueDateTime`, ConnectWise
  `requiredDate`). If the PSA refuses, its words are shown and nothing changes here; if it cannot
  be reached, that is an error and nothing changes here. Only once the PSA has accepted is the
  date kept, and the next sync reads it back as the PSA holds it, so the two cannot diverge. The
  certification suite holds every connector to "what was set is what is read back".
- **Board ticket:** the date is the portal's own and is set directly.

Either way the ticket keeps **the date it was first due** (`OriginalSlaDueAt`, set on the first
extension and never changed after), how many times it was moved, and the last move's reason,
author and time. The audit log records every move with its reason (`ticket.due_date.extended`),
which is what the ticket's history shows.

A client is sent none of it: not the reason, not the count, and not the due date, which was
already staff-only on a ticket's detail. Nothing changes for clients.

Refused before anything is sent: a date that is not later than the current one ("to bring it
forward, ask an administrator"), a date in the past, a missing or over-long reason, a finished
ticket. A ticket with no due date at all can be given one.

## Figures

The SLA figures (client workload, technician metrics, the reports) measure against the ticket's
due date as it stands, which after an extension is the extended one. The original date is kept on
the ticket so that a report can tell an extended ticket from one that was simply on time; the
reports do not yet show that distinction. That is the honest state: an extended ticket that is
closed before its new date counts as within SLA, and its extension is visible on the ticket and
in the audit, not in the figures.

## Code

`Ticket` (six new columns, migration `TicketDueDateExtension`, additive), `UnifiedTicketUpdate.DueDate`
and its handling in the Autotask, ConnectWise and mock connectors, `ITicketDueDateService` /
`TicketDueDateService`, `POST api/tickets/{id}/due-date` (`TicketDueDateController`, the same
scope and permission as a status change), `TicketDetailDto.DueDate` (staff only), and the ticket
page's control. Tests: `TicketDueDateTests`, the certification test
`A_due_date_set_from_the_portal_is_what_is_read_back` on all three connectors, and the browser
spec `due-date.spec.ts`.

## Not done here

- Bringing a due date forward, or clearing one. The action is an extension.
- Queueing the write for when the PSA is away. Phase 9's outbound queue (slice 19) is not merged
  yet; when it is, this becomes one more kind of change it can hold.
- The reports do not yet single out extended tickets (see Figures).
- The real Autotask and ConnectWise are not exercised: the field names come from their
  documentation and the fakes. Part of the blocked real-PSA certification.
