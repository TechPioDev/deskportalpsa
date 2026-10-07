# A PSA's custom fields

Phase 9. A PSA account has fields of its own that no other has: an asset tag, a cost centre, a
site code, a note the engineers keep for themselves. The portal read none of them. The owner
decided on 6 October 2026 (decision 5 in section 20a of the
[audit](PHASE9_PSA_INTEGRATION_ARCHITECTURE_AUDIT.md)) that they are not brought in wholesale and
none is hard-coded: an administrator chooses, field by field, whether to bring one in, what it is
called here, and whether the client may see it, with staff only as the default.

The code is `PsaCustomField` (the decision), `CustomFieldService` (making it),
`CustomFieldValues` (what is stored), a few lines of `TicketSyncService` (what is kept as a ticket
arrives), `TicketReadService.CustomFieldsOfAsync` (who is shown what), the two connectors, and
`CustomFields.tsx` (the Custom fields tab of Field Mapping). The tests are `CustomFieldTests` (on
a SQL translator), the connector certification suite, and the last browser test in
`e2e/connections.spec.ts`.

## Nothing is hard-coded

The portal does not know any custom field by name. The list on the page is what the connection's
PSA says it has, asked when the page is opened:

| | How the fields are listed | How a ticket carries a value | A field is known by |
|---|---|---|---|
| Autotask | The ticket entity's user-defined fields (`entityInformation/userDefinedFields`), with each one's label, kind and, for a list, its values | `userDefinedFields` on the ticket, by name. A list's value is an id and is read as its word | Its name |
| ConnectWise | Read off a ticket: ConnectWise sends every one of the tenant's fields on each ticket, with its caption and kind | `customFields` on the ticket | Its id (a caption can be changed; an id cannot) |

A connection still being set up can be asked too, the way the setup wizard asks it, so fields can
be chosen before the first import.

Until this slice neither connector read a custom field's value off a ticket, and ConnectWise said
it read none. Both now do, and both say so; the certification suite holds it against stand-ins.

## Three decisions for each field

| | Choices | Default |
|---|---|---|
| **Bring in** | Import, or ignore | **Ignore.** A field nobody has decided about is ignored |
| **Called in the portal** | Any name | What the PSA calls it |
| **Who sees it** | Staff only, or staff and the client | **Staff only** |

And one that is not a choice: **changing it.** Every imported field is **read-only**. The portal
writes no custom field to a PSA, so a value is changed in the PSA and read from it. The page says
so on each row. If writing one is ever built, it will be a capability a connector has to claim
and a permission of its own, not a tick-box that was already there.

## What is kept, and what is not

- **A field that is not chosen is not stored.** Its value is on the ticket the PSA sent and is
  dropped there. What a tenant puts in a field nobody asked for is not the portal's to hold.
- A chosen field's value is kept on the ticket (`tickets.CustomFieldsJson`, one JSON object of
  field key to value), only where it has one. A ticket with none holds nothing.
- A value follows the PSA: changed there, it changes here the next time the ticket is read;
  cleared there, it goes.
- Values are text as the PSA gives them, up to 2,000 characters each. A list's value is its word.
  A date is as the PSA writes it; the portal does not re-format it.

## Who is shown what

**Decided when a ticket is read, from the settings as they are then.** Not from what happens to
be stored.

- **Staff** see every imported field that has a value on the ticket, and which of them the client
  sees too.
- **A client** is sent only the fields marked "staff and the client". A staff-only field is not
  in what a client is sent in any form: not its value, not its name. The settings are filtered in
  the query, so its value is not even read on a client's request. Tests serialise what a client
  is sent and search it for the staff-only value; they fail if the filter is taken out.
- **Taking a field back from clients is immediate.** No sync is needed: the next time the client
  opens the ticket it is not there.
- **Setting a field to ignore is immediate too**, for everybody. The stored copy goes the next
  time each ticket is read from the PSA.
- **Choosing a field** reaches a ticket the next time that ticket is read from the PSA. A ticket
  nothing changes on waits for "Re-sync all".

Showing a field to clients is a second, separate choice. On the page it cannot be saved until a
sentence naming the fields has been ticked: "Clients will see … on their own tickets, with
whatever the PSA holds in that field. I have checked that is fit for them to read."

## One connection's, one organization's

A decision is for one field of one connection. The same field name on another account of the
same PSA is that account's to decide. A connection of another organization is not found, for
reading and for saving, and the sync, which runs with the tenant filter off, names the
connection's own organization when it reads the decisions.

## Who may, and what is recorded

| | Permission |
|---|---|
| See the fields and what is decided | `mappings.view` |
| Decide | `mappings.manage` |

- `customfields.changed` is written when a save changed anything, with what was brought in,
  ignored, renamed, shown to clients and taken back from them.
- `customfields.shown_to_clients` is written as well whenever a field becomes visible to clients,
  naming the fields, so that "who decided clients may see this" is one search.
- A save that changes nothing writes nothing.

Everything wrong with a save is said at once and none of it is saved: a field the PSA does not
list (a field cannot be invented), one set to be ignored and also shown to clients, two imported
fields under one name, a name longer than 200 characters.

## When the PSA cannot be asked

The page shows the decisions already saved and says why the list is short: the PSA did not
answer, or the connection is switched off. A decision can still be changed or undone, which
matters most for taking a field back from clients. A new field cannot be chosen blind.

## The interface

`/api/admin/connections/{connectionId}/custom-fields`

| | | |
|---|---|---|
| `GET` | `mappings.view` | The PSA's fields, each with what is decided, and how many are brought in and shown to clients |
| `PUT` | `mappings.manage` | Decisions about some fields. A field left out keeps what was decided before. All or nothing |

One table, `psa_custom_fields`, and one nullable column, `tickets.CustomFieldsJson` (migration
`PsaCustomFields`, additive: no existing row is changed, and nothing is imported until chosen).

## Not in this slice

- **Writing a custom field to a PSA.** Read-only, as above.
- **Mapping a custom field onto one of the portal's own fields** (a PSA field that becomes the
  ticket's priority, say). The "target" here is a name: an imported field is shown under the name
  chosen for it, as a field of its own. Nothing existing is overwritten by one.
- **Filtering, searching or reporting by a custom field.** Values are on the ticket page.
- **Custom fields of companies, contacts or devices.** Tickets only.
- **Formatting by kind.** Yes/no reads "Yes" or "No"; a number and a date read as the PSA wrote
  them.
- **Proved against a real PSA.** Both connectors are held to stand-ins that send custom fields in
  the shape each PSA documents. That a real Autotask sends `userDefinedFields` on a ticket query
  and a real ConnectWise sends `customFields` on every ticket, as assumed, is part of the
  certification that waits on the owner's test environments: BLOCKED — TEST ENVIRONMENT REQUIRED.
  If either assumption is wrong the effect is that no field is listed or no value arrives; nothing
  is shown that should not be.
