# Changes that wait for a PSA

Phase 9, slice 5. A change made in the portal while its PSA cannot be reached is kept and sent
later, instead of being handed back as an error.

The owner decided on 6 October 2026 (decision 2 in section 20a of the
[audit](PHASE9_PSA_INTEGRATION_ARCHITECTURE_AUDIT.md)): queue the outbound changes that support
it, with three states (Pending Sync, Synced, Sync Failed); keep the local side only as the
[ownership matrix](PHASE9_SYNC_OWNERSHIP_MATRIX.md) allows; retry with a growing wait and some
spread, inside the provider's rate limits; keep what fails; let an authorised person retry;
never call anything synced before the provider has confirmed it.

The code is `OutboundOperation` (the record), `OutboundQueue` (keeping, taking, settling),
`OutboundProcessor` (sending each kind), `OutboundRunner` (one turn, for every organization), the
worker's `OutboundQueueService`, and the places a change is made: `TicketCommandService` (a reply, a note, a client's comment, a new ticket),
`TicketStatusController` (a status) and `TicketTimeWriter` (an hour). The tests are
`OutboundQueueTests` (on a SQL translator) and `e2e/outbound-queue.spec.ts`.

## What used to happen

Every write to a PSA was made inside the request. A PSA that did not answer meant an error on
the screen and nothing kept: a reply somebody had typed was gone. (Two things were already kept:
an hour logged and a ticket raised were stored and marked as failed, for somebody to notice and
retry by hand.)

## Three states, and what each means

| State | Means | Becomes |
|---|---|---|
| **Pending Sync** | Kept. Waiting to be sent, or to be tried again | Synced, or Sync Failed |
| **Synced** | The PSA has it, **and said so** | Nothing further. A synced change is simply the ticket as it is |
| **Sync Failed** | The PSA refused it, or every try ran out. Kept | Pending Sync again, if a person retries; gone, if a person lets go of it |

There is no fourth state and no "probably synced". An operation becomes Synced in one place in
the code, after the PSA's answer.

## Which changes wait, and what is kept of each

What the portal keeps of a change on its own side depends on who owns what.

| Change | While the PSA is away | Why |
|---|---|---|
| **A reply or an internal note** by staff | The note is stored at once, marked Pending Sync, and shown to staff in the thread with that mark. **It is not shown to the client** until it is Synced | The words are the author's work. But the PSA has not sent them to anyone, and may never |
| **A client's comment** | Stored, and shown to that client marked as waiting | It is their own comment |
| **A status change** by staff | **The ticket keeps the status it has.** What was asked for is kept as asked for, and becomes the ticket's status only when the PSA accepts it | The PSA holds a ticket's status (the matrix: "PSA wins"). Asked for while the PSA is away, it is not written over what the PSA last said |
| **An hour logged** | The entry is stored as Pending (it was already stored; it was marked failed) and is retried by itself | Local first, as before |
| **A ticket raised**, where the PSA could not be connected to at all | The ticket is stored as waiting and created in the PSA when it can be | Local first, as before. See "Never twice" for when it is not retried |

Not queued, and refused as before when the PSA is away: reassigning a ticket or moving it to
another queue, changing or deleting a time entry, changing a ticket's device, and attachments
(which were already stored first and are not retried). Each of those is the PSA's to hold or has
its own path; they are listed in the delivery tracker as not built.

### A status is the PSA's

This is the rule the owner named: a field the PSA is the authority for is not changed locally
because the PSA could not be reached.

- The ticket goes on showing the status the PSA last gave it. The page says the change is kept
  and will be sent.
- Everything the portal itself requires of the change (a resolution where the board asks for one,
  no open tasks, review before closing) is checked when it is asked for, before the PSA is called.
- **When its turn comes, it is sent only if the ticket is still as it was.** If the ticket's
  status has changed since (the PSA moved it, a sync brought that in), the waiting change is
  failed with the reason and nothing is sent: what somebody wanted an hour ago is not set over
  what the ticket says now. If the ticket already has the status asked for, there is nothing to
  send and it is done.
- One status change waits for a ticket at a time. A second is refused until the first is
  answered or let go of.
- The credit for a resolution is the person's who asked, though the worker sent it.

## Only when the PSA is away

A change waits when the PSA **could not be reached**: it did not answer, it reported a fault of
its own, or it turned the request away for its rate. A PSA that answers and says no has
answered: the refusal is shown at once, as before, and nothing is queued. Retrying a refusal
unchanged would only be refused again.

## The waits

Thirty seconds after the first failure, then twice as long each time, up to an hour: 30 s, 1,
2, 4, 8, 16 and 32 minutes. Each wait is spread by up to a fifth either way, so that a PSA coming
back is not met by everything at once. **Eight tries in all**, over about an hour; then the
change is Sync Failed, and kept.

Where the PSA says when to come back (a rate limit with a time), that is the wait. The
connectors' own limit on requests a minute applies to these sends as to every other call.

Of one ticket's changes, the oldest goes first, and the next not until it has gone: a status
asked for after a reply is not sent before the reply.

The worker tries what is due every five seconds. A change is taken by a save of its own, checked
by a version and held for five minutes, so two workers would not send one change, and a worker
that stops does not strand it.

**Each change is sent inside its own organization.** The worker asks once which changes are due,
with every organization in view, and then takes each in a unit of work scoped to the organization
it belongs to, as a request from that organization would be. What is read while sending it is that
organization's, and what is written, the audit entry above all, carries that organization. (Written
with every organization in view, an audit entry belongs to none and nobody ever reads it. The first
version of this slice did that; the review before it was committed found it, and
`The_worker_sends_each_change_inside_its_own_organization…` now holds it, with two organizations.)

**A fault of the portal's own is a try.** If sending a change breaks inside the portal (not the
PSA's answer, and not the PSA being away), nothing half-done is saved, the try is counted, and the
change waits its turn like any other. It runs out of tries and is failed and kept; it is not taken
again every few minutes for ever. What it says on the ticket is that the portal met a fault of its
own; what the fault was is in the portal's log. Since it is not known whether anything reached the
PSA, it is treated as possibly sent.

A change on a connection that has been **disabled** is failed when its turn comes, with the
connection's name, and kept: a disabled connection sends nothing. It can be sent again once the
connection is enabled. A paused connection goes on sending, as it does for everything else.

## Never twice

A request that was sent and never answered may have been carried out. Sending it again could
say a reply to a customer twice, bill an hour twice, or open a second ticket. So:

| | Before it is sent again after an uncertain failure |
|---|---|
| A note | The PSA's thread is read. A note with the same words, on the same side, written no earlier, and not already known here as another of the portal's notes, **is** this note: it is adopted and nothing is sent. If a sync has meanwhile stored the PSA's copy as one of the PSA's own, that copy is removed, so the thread says it once |
| An hour | The PSA is asked whether it has the entry (as the retry on the ticket already did) |
| A status | Sent again. Setting a status twice is setting it once |
| A new ticket | **Not sent again by a machine.** There is no way to ask a PSA "did you create this?", so a ticket that may have been created is left as it always was: stored, marked in error, listed for staff, for a person to look in the PSA and resend. Only a ticket whose request certainly never left (the PSA could not be connected to, or turned it away for its rate) is retried automatically. A waiting ticket whose try broke inside the portal is in the same doubt: it is failed with that said, and created only when a person sends it again |

"Certainly never left" is decided from the failure itself: the name could not be resolved, the
connection could not be made, or the request was refused for its rate.

## A person's decision

A failed change is not retried by itself and is not deleted. On its ticket, whoever may change
that ticket can:

- **Send again**: back to Pending Sync from the first try. (A waiting one can be brought forward
  with "Try now".)
- **Let go**: it is not sent. A reply that was never sent is removed with it, so the thread does
  not go on showing words the customer never got. Asked for confirmation first.

Both are reached by the ticket's address and only for a change that is that ticket's. A change
that is already Synced can be neither retried nor taken back from here. Neither can one a worker
is sending at that moment: freed then, it could be taken by a second worker and sent twice. The
answer is to try again in a minute.

## Where it shows

- **On the ticket**, for staff: a panel "N changes are not in the PSA yet", each with its state,
  who made it, how many tries, when the next is, and why the last failed.
- **On the note**: "Pending Sync" or "Sync Failed" beside the author.
- **Unsent changes** (under Integration Health in the menu): every change the PSAs do not have,
  what has failed first, each leading to its ticket.
- **The attention list**: failed changes at once; waiting ones only after an hour.
- A client sees their own comment marked as waiting, and nothing else of this.

## What is recorded

| Action | When |
|---|---|
| `outbound.queued` | A change was kept because the PSA could not be reached, with who made it and why |
| `outbound.failed` | It was refused, or ran out of tries |
| `outbound.retried` | Somebody sent it again, and who |
| `outbound.discarded` | Somebody let go of it, who, and what it was |

A change that syncs writes no entry of its own: the change itself is recorded where it always
was (a status change is audited as a status change when the PSA accepts it).

## Not in this slice

- **Reassignment, queue moves, edits and deletions of time, device changes, attachments.** Not
  queued; see above.
- **A client is not told their new ticket is "waiting"** in so many words on its page. It is
  listed for them and has no PSA number until it is created.
- **A first-response time** is stamped when a waiting reply is sent, not when it was typed.
- **Webhooks.** Nothing here depends on them.
- **Proved against a real PSA.** How a real Autotask and a real ConnectWise fail (what a timeout,
  a 5xx and a rate limit look like from each, and whether a note sent with a lost answer is found
  again by its words) is held here against a stand-in. It is part of the certification that waits
  on the owner's test environments: BLOCKED — TEST ENVIRONMENT REQUIRED.
- **In the browser, against a PSA that is away.** The browser suite has no PSA ticket to change.
  The screens are driven over stood-in answers on a real board ticket; what the queue does is
  held by `OutboundQueueTests`.

One migration, `OutboundQueue`: one table and one nullable column on notes. Additive; nothing
existing is changed, and nothing waits until a PSA is away.
