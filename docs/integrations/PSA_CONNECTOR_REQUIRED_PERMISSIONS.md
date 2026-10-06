# What a PSA connection needs to be allowed to do

The API account behind a PSA connection should be able to do what the portal does with it and
nothing more. This is the complete list of what each connector reads and writes, taken from the
connector code (`packages/connectors`), so it can be checked against the code rather than believed.
If a route is not here, the portal does not call it.

Give the API account these and no others. How a permission is named and where it is set is the
PSA's own business and changes between versions; this document says **what** must be allowed, in
the PSA's own entity and route names.

Field ownership - which side is the record for each field - is in
[PHASE9_SYNC_OWNERSHIP_MATRIX.md](PHASE9_SYNC_OWNERSHIP_MATRIX.md).

## What the portal never does, in any PSA

- Delete a ticket, a company, a contact, a note, an attachment or a configuration.
- Create or change a company, a contact, a technician, a contract or a configuration.
- Change a PSA's settings, statuses, queues, boards or picklists.
- Read anything about billing beyond a ticket's time entries, the work types and roles time can be
  logged against, and a company's agreements.

Two operations remove something, both started by a person and both audited:

| Operation | When |
|---|---|
| Delete one time entry | Someone with the right deletes an hour they (or, with a reason, a colleague) logged from the portal |
| Unlink a configuration from a ticket (ConnectWise) | A technician changes which device a ticket is about; the previous link is removed. The configuration itself is untouched |

No delete is ever a consequence of synchronization.

## Autotask (REST API v1.0)

Autotask does not accept its API-only user as the owner of a time entry, so a connection also names
a real technician ("default time-entry resource") and that technician must hold an active work role.

### Read

| Entity | Used for |
|---|---|
| `Tickets` (query, by id) | The tickets themselves |
| `Tickets/entityInformation/fields`, `…/userDefinedFields` | Status, priority, queue and category lists; custom-field names |
| `TicketNotes` | The conversation on a ticket |
| `TicketAttachments` (query; a file's bytes through `Tickets/{id}/Attachments/{id}`) | Files on tickets |
| `TimeEntries` | Worked, billable and non-billable totals; the notes written on time entries; checking whether a retried entry is already there |
| `Companies` | Client names; the connection test |
| `Contacts` | Who a ticket is for; a company's contacts |
| `Resources`, `ResourceRoles`, `Roles` | Technician names; which queues a technician covers; which role a time entry may carry |
| `ConfigurationItems` (and its `entityInformation/fields`) | A company's devices; a ticket's device |
| `Contracts` (and its `entityInformation/fields`) | A company's agreements, for people who hold that part of the control panel |
| `Holidays` | The PSA's holiday calendar |
| `BillingCodes` | Work types offered when logging time |

### Write

| Route | Used for |
|---|---|
| `POST Tickets` | A ticket raised in the portal |
| `PATCH Tickets` | Status, assignment, queue, and the device a ticket is about |
| `POST Tickets/{id}/Notes` | A reply or an internal note |
| `POST Tickets/{id}/Attachments` | A file uploaded in the portal |
| `POST TimeEntries`, `PATCH TimeEntries` | Logging and correcting time |
| `DELETE TimeEntries/{id}` | Deleting one portal-logged hour (see above) |

### What can be withheld

| Withhold | Effect |
|---|---|
| `TimeEntries` (read and write) | No time totals, no time logging to Autotask. Logged hours stay in the portal as failed pushes |
| `TicketAttachments` | Files are not mirrored either way |
| `ConfigurationItems` | No devices |
| `Contracts`, `Holidays` | Those two lists are empty |
| Write to `Tickets` and notes | The portal becomes read-only for this connection; every change is refused with Autotask's own message, and nothing is saved locally |

A read that is refused for every ticket is reported once per sync run and switched off for that run
([sync-engine.md](sync-engine.md)); it is not filed against each ticket.

## ConnectWise PSA (REST API 3.0)

Authentication is the company id, an API member's public and private keys, and a `clientId`.

### Read

| Route | Used for |
|---|---|
| `service/tickets` (list, by id) | The tickets themselves |
| `service/tickets/{id}/notes` | The conversation on a ticket |
| `service/tickets/{id}/configurations` | The devices on a ticket |
| `service/boards`, `…/{id}/statuses`, `…/{id}/types`, `…/{id}/teams` | Boards, their statuses and types; which board a member covers |
| `service/priorities` | Priorities |
| `system/documents` (list, by id, `…/{id}/download`) | Files on tickets |
| `time/entries` | Time totals; time-entry notes; checking whether a retried entry is already there |
| `time/workTypes`, `time/workRoles` | Offered when logging time |
| `company/companies` | Client names; the connection test |
| `company/contacts` | A company's contacts |
| `company/configurations` | A company's devices |
| `system/members` | Technician names; the connection test |
| `finance/agreements` | A company's agreements |
| `schedule/holidayLists`, `…/{id}/holidays` | The PSA's holiday calendar |

### Write

| Route | Used for |
|---|---|
| `POST service/tickets` | A ticket raised in the portal |
| `PATCH service/tickets/{id}` | Status, priority, board, owner |
| `POST service/tickets/{id}/notes` | A reply or an internal note |
| `POST service/tickets/{id}/configurations`, `DELETE …/configurations/{id}` | Setting or changing the device a ticket is about |
| `POST system/documents` | A file uploaded in the portal |
| `POST time/entries`, `PATCH time/entries/{id}` | Logging and correcting time |
| `DELETE time/entries/{id}` | Deleting one portal-logged hour (see above) |

### What can be withheld

| Withhold | Effect |
|---|---|
| `time/*` | No time totals, no time logging to ConnectWise |
| `system/documents` | Files are not mirrored either way |
| `company/configurations`, ticket configurations | No devices |
| `finance/agreements`, `schedule/*` | Those two lists are empty |
| Write to `service/tickets` and notes | The portal becomes read-only for this connection |

## Where the connection may point

The address of a connection is checked when it is saved: `https`, no credentials or query in it,
not a private address, and an Autotask connection must be on `autotask.net`. At connect time the
portal refuses any private or reserved address and follows no redirect. A self-hosted ConnectWise
on a private network is allowed by naming its host in `Connectors:AllowedHosts` on the server.

## Keeping the credentials

Credentials are entered in the portal, encrypted before they are stored (AES-256-GCM), and never
returned: the screens show only which fields hold a value. They are sent to the connection's own
address and nowhere else, as headers, and are never logged. Rotating one field keeps the others.
