# The person a ticket is assigned to is told

The owner asked on 13 October 2026, after raising TECPIO-000003 for a colleague with two people in
CC: the two in CC were e-mailed, the person it was assigned to heard nothing.

## What was there before

Nothing by e-mail. The only signal was a notification on a device, and that had two limits:

- the person must have switched notifications on from their Profile page (1 of 41 staff had);
- it compares a ticket with how it last looked, so a ticket that is assigned as it is raised has
  no "before" to differ from, and said nothing.

## What happens now

When somebody assigns a ticket to a colleague in the portal, that colleague gets one e-mail:
"Harpal Singh assigned TECPIO-000003 to you", the ticket's title, and a button that opens it.

- **When:** as a ticket is raised with an assignee, when a ticket is passed on, and when work is
  planned into somebody's time. Within about two minutes.
- **Not sent** when a person takes a ticket themselves, when the ticket went back to the board,
  when it is already finished, when it was passed on again before the e-mail went (only the person
  it rests with is told), or to someone who has been deactivated.
- **What it says:** who assigned it, the ticket's number and title, the link. Nothing else from the
  ticket, and not the handover note: those are read on the ticket.
- **A device** is now also told about a ticket assigned as it is raised, where the person switched
  notifications on and did not switch "assigned" off. A ticket that changes hands later was already
  reported to a device and still is, once.
- The wording is under **E-mail wording** as "Ticket: assigned to you" and can be changed there.
- The form says so under "Assign to".

## How it works

Every assignment made in the portal already writes a handover row (`ticket_assignments`: from, to,
by whom, note). `TicketAssignmentNotifier` reads the rows it has not dealt with, decides, mails, and
stamps `NotifiedAt` on each. It runs in the worker in the same two-minute turn as the CC notices
(`TicketFollowerNotifyRunner`), the assignee first.

Migration `TicketAssignmentNotices` adds `ticket_assignments.NotifiedAt` and stamps every existing
row, so nobody is mailed about tickets they were given before today.

## Limits, said plainly

- An assignment made **inside a PSA** (a dispatcher assigning in Autotask or ConnectWise) writes no
  handover row and sends no e-mail. A device is still told, as before.
- A team assignment mails nobody: there is no one person to tell.
- An e-mail that could not be sent is logged in the mail log and not retried. The ticket is in the
  person's list either way.
- The e-mails leave by the organization's own mail setup. Until its SPF record names the server,
  they can land in Junk.

## Tests

`TicketAssignmentNoticeTests` (15, on a SQL translator). Each rule above was checked by removing it
and seeing a test fail.
