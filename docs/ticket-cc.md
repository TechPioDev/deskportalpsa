# Keeping colleagues informed on a ticket (CC)

The owner asked for it on 12 October 2026: when a ticket is raised on an internal board there is
"Assign to" for one person, and no way to copy anybody else in.

## What a person sees

On the board's **New ticket** form, under "Assign to", there is **Keep informed (CC)**. Pick one
colleague after another; each appears as a chip that can be taken off again. The assignee is not
offered there, because they hear about their own ticket anyway.

The people named become the ticket's **followers**, exactly as if each had been added from the
ticket's **Following** box afterwards. A follower is not an assignee: the ticket is not theirs and
nothing about who holds it changes.

## What a follower is told

Following used to put a ticket in a person's "Following" list and nothing more. Now:

| When | What they get |
|---|---|
| Somebody else adds them (on the form, or later from the ticket) | One e-mail: who added them, to which ticket, and that it is not assigned to them. |
| Somebody else replies, or adds an internal note, or the client replies | One e-mail listing who did what since the last one. |
| The ticket is resolved or closed | The same e-mail, saying so. |

- Several things inside one turn make **one** e-mail. More than eight are counted, not listed.
- Their **own** reply is not news to them.
- Nobody is told that they followed a ticket themselves.
- What happened **before** they started following is not sent.
- A person with notifications switched on for a device gets the same news there as well.

The e-mail names the ticket and who did what. It **quotes nothing** from the thread: an internal
note stays on the ticket, where the reader's own access decides what they see.

To stop: open the ticket and press **Unfollow**.

## The wording

Both e-mails are in **E-mail wording** with the others, and can be reworded, previewed and sent as
a test: "Ticket: you were added" and "Ticket: news for followers". They offer the person's name,
the ticket's number and title, who added them or the list of updates, and the link.

## How it works

`TicketFollowerNotifier`, run by the worker every two minutes for each organization
(`TicketFollowerNotifyService`). It looks at the follower rows rather than hooking the places that
write a note: a reply can come from the portal, from a client, from a PSA at its next sync, or from
the queue that sends later, and a follower is owed the same message whichever it was.

Each follower row remembers two things (migration `TicketFollowerNotices`):

- `AddedNotifiedAt`: the question "do they know they were put on it?" is settled.
- `NotifiedThrough`: everything up to this moment has been reported to them.

A turn reports what is older than ten seconds (`Settle`). A note is stamped when it is written and
visible a moment later, when its save completes; reporting right up to "now" would step over one
stamped just before a turn and saved just after it, and it would never be told.

A message that cannot be mailed is written to the e-mail log and is **not** tried again: the news
is on the ticket, and a late repeat is noise. People who already followed a ticket before this
arrived are not told so, and are not sent the ticket's past: for them the count starts at the
migration.

## Limits, said plainly

- At most 20 people can be kept informed on one ticket from the form.
- A note carries its author's **name**, not an id (it may have come from a PSA). "Their own reply"
  is therefore recognised by name; two members of staff with the same display name would not be
  told of each other's replies.
- The e-mails go to staff addresses. Today the mail account delivers to `@techpio.com` only, and
  lands in Junk until the SPF record of `techpio.com` includes the server (see the owner's guide).
- There is no per-person switch for these e-mails beyond unfollowing the ticket.
- Clients are not followers. Telling a client about an internal ticket is a separate decision
  (option C of 12 October), not built.

## Tests

`TicketFollowerNoticeTests` (16, on a SQL translator): naming people on the form, the refusals, each
message and its once-only rule, a follower's own reply, what came before they followed, the settling
time, a long day in one message, someone who has left, a mail failure, a device, another
organization, and the two templates. `ticket-cc.spec.ts` drives the form in a browser.
