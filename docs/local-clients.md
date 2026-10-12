# Clients that are in no PSA

The owner asked for it on 12 October 2026. The "For a client" list on a board ticket held only
the companies a PSA had sent. The team's own work for NeedTech could not say so, because NeedTech
is not set up in the PSA, and there was no way to add them.

## What a person sees

**On the ticket form** (a board's New ticket), under "For a client":

- the list is in two groups, **From your PSA** and **Added by hand (not in a PSA)**;
- the people who look after the boards (`boards.manage`) see **The client is not in the list —
  add them**: a name, **Add client**, and the new client is chosen for the ticket being written.

**On the Boards page**, the same people see **Clients not in a PSA**: every client added by hand,
how many tickets name it, and Rename, Retire and Bring back. A client can also be added there.

## What such a client is, and is not

It is a name the desk keeps, so that its own tickets, and the reports built from them, can say who
the work was for.

- **It is never told anything.** Naming a client on a board ticket shows that client nothing and
  sends them nothing. That was already true for a PSA's client named on a board ticket, and it is
  just as true here.
- **It has no client sign-ins.** It is not offered on the Client access page, and a grant to it is
  refused: it has nothing a client could be shown.
- **Nothing is asked of a PSA about it.** No devices, no contacts, no account settings: those calls
  are refused with a sentence saying why, not attempted.
- **A sync does not touch it.** A PSA's company is matched by its connection and the PSA's own id;
  a client added by hand has neither.

## The rules

- **One name, one client.** A name already in the list is refused, whatever the case, whether it
  came from a PSA ("it comes from your PSA (ConnectWise). Choose it there") or was added by hand.
  Two entries with one name would split a client's work between them in every report.
- **Retire, not delete.** A retired client is not offered and cannot take a new ticket. The tickets
  that name it keep its name. Adding the same name again is refused: bring it back instead.
- **A PSA's client is not renamed here.** Its name is the PSA's.
- Names are 2 to 200 characters; spaces at the ends and doubled spaces are tidied.
- Adding, renaming, retiring and bringing back are each written to the audit log
  (`client.local.added`, `.renamed`, `.retired`, `.restored`).

## How it is stored

A client company used to require a PSA connection. `client_companies.PsaConnectionId` is now
optional (migration `ClientsOutsideAPsa`: the column loses its NOT NULL, nothing else changes, and
no row is touched). A client added by hand has no connection, and carries `local:<id>` where a
PSA's own id would be, which keeps the existing unique index meaningful.

Every place that assumed a connection was found by the compiler and decided one at a time:
connection statistics count only a PSA's companies; account settings, device sync and client access
refuse a company that has none.

## Limits, said plainly

- If the same client is later set up in the PSA, the PSA's company arrives as a **second** entry
  with the same name, because a sync adds what the PSA sends. Retire the one added by hand. The
  tickets raised for it stay with it: there is no merge yet.
- Only board tickets can name such a client. A PSA ticket's client is the PSA's.
- There is no contact or e-mail address kept for it. Telling a client about an internal ticket is a
  separate decision (option C of 12 October) and is not built.
- Rolling the migration back needs the hand-added clients removed first: the column cannot be made
  required again while a row has no connection.

## Tests

`LocalClientTests` (14, on a SQL translator) and `local-clients.spec.ts` (2, in a browser).
