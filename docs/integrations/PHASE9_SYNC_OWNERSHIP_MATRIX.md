# Sync ownership matrix (Phase 9)

Which side owns each piece of data, which way it flows, and what happens when the two disagree.
Written from the code as it stands on 5 Oct 2026 (main `9786eec`), before any Phase 9 code; the
**As built** columns are facts with their source, the **Phase 9** column is the rule the framework
must keep or introduce. No bidirectional behaviour is to be added without a row here first.

Companion to [PHASE9_PSA_INTEGRATION_ARCHITECTURE_AUDIT.md](PHASE9_PSA_INTEGRATION_ARCHITECTURE_AUDIT.md).

## How the two directions work today

**PSA → PIO (inbound).** A worker polls every enabled connection every five minutes
(`PollingSyncService`, `Sync:PollIntervalMinutes`). `ConnectionSyncRunner` pages the provider's
tickets changed since the connection's last successful sync and `TicketSyncService` upserts each one,
identified by **connection + external ticket id**. The provider's values overwrite the portal's copy
of the fields it owns. A hash of those fields makes an unchanged ticket a no-op.

**PIO → PSA (outbound).** There is no outbound queue. A portal action that changes PSA data calls
the provider **inside the request**, and the pattern differs by kind of data:

| Pattern | Used for | What a provider failure does |
|---|---|---|
| **Provider first** (write-through) | Status, assignment, queue, replies and notes, time-entry change and delete | The request fails with the provider's message and **nothing is saved locally**. The portal never shows a value the PSA did not accept |
| **Local first, then push** | A ticket raised in the client portal; a time entry logged in the portal; an uploaded attachment | The local record is kept and marked (ticket `SyncStatus = Error`; time entry `Failed`; attachment audited as `attachment.push_failed`) and can be pushed again |

**Echo suppression.** A portal write is recorded in `sync_events` with the hash of the state it
produced; when the same state comes back on the next poll the upsert recognises it and does nothing
(`TicketSyncService` → `IsPortalEchoAsync`). Notes, attachments and time entries are recognised by
the provider's own id, stored when the provider accepted them.

## Ticket fields

| Field | Direction (as built) | Authority (as built) | Source | Phase 9 rule |
|---|---|---|---|---|
| Title (subject) | PSA → PIO | PSA | `TicketSyncService` overwrites on every changed sync | Unchanged. Editing a PSA ticket's title in PIO is not offered |
| Description | PSA → PIO | PSA | same | Unchanged |
| Status | PSA ↔ PIO | **PSA**, write-through | In: mapped through the `status` rules. Out: `TicketStatusWriter` maps the portal value to the provider's and calls the provider first | Unchanged. PSA wins: a status set in PIO exists only once the PSA accepted it, so the two cannot diverge. An unmapped inbound value must be flagged, not guessed (see Mapping) |
| Priority | PSA → PIO (set once outbound, when PIO creates the ticket) | PSA | In: `priority` rules. Out: only on create | Unchanged: PSA → PIO |
| Queue / board | PSA ↔ PIO | **PSA**, write-through | In: `queue` rules. Out: `TicketAssignmentController` (reassign) | Unchanged |
| Category (issue / type) | PSA → PIO (set once outbound on create) | PSA | In: `category` rules, raw kept in `PsaCategory` | PSA → PIO. Work type (type and subtype) is added by mapping, not by a second writer |
| Assigned technician (the PSA's resource / member) | PSA ↔ PIO | **PSA**, write-through | In: `AssignedTechnicianExternalId` + name. Out: reassign, with the role the PSA requires | Unchanged |
| Portal holder (`AssignedAppUserId`), handovers, followers | PIO only | PIO | Never sent | **PIO only.** Planning and "Take it" never change the PSA's assignee |
| Requester name and e-mail | PSA → PIO | PSA | Overwritten when the provider names a contact | Unchanged |
| Client company | PSA → PIO | PSA | Found or created by connection + external company id | PSA → PIO; creation becomes an explicit mapping decision (see Clients) |
| Created date (`PsaCreatedAt`) | PSA → PIO | PSA | Never defaulted to "now" | Unchanged |
| Resolved / closed dates | PSA → PIO | PSA | Overwritten from the provider | Unchanged |
| Due date / SLA due (`SlaDueAt`) | PSA ↔ PIO | **PSA**, write-through | In: from the provider's own target. Out: **only an extension asked for by a person with a reason** (`TicketDueDateService`), provider first; the PSA's refusal changes nothing here. See [due-date-extension.md](../due-date-extension.md) | PSA authoritative. PIO never computes or writes an SLA to a PSA; it may move a ticket's due date later on request, and the next sync reads back what the PSA holds. The original date, the count and the reason stay PIO-only |
| First-response promise, SLA pause | PIO only | PIO | Board and monitoring tickets only | PIO only |
| Device | PSA ↔ PIO | **PSA**, write-through | In: kept when the provider does not carry it. Out: a technician sets or changes it (`TicketDeviceService`, provider first); in ConnectWise the previous configuration is unlinked from the ticket, the configuration itself untouched | Unchanged |
| Time totals (worked, billable, non-billable) | PSA → PIO | PSA | Recomputed from the provider's entries on each sync of the ticket | Unchanged |
| Resolution text, resolved-by, reopen count, review state | PIO only | PIO | Never sent | PIO only |
| Portal custom fields, tasks, links, approvals, satisfaction | PIO only | PIO | Never sent | PIO only. A PSA custom field mapped in Phase 9 is **PSA → PIO, read-only** in its first version |

## Notes, attachments, time

| Data | Direction (as built) | Authority | Source | Phase 9 rule |
|---|---|---|---|---|
| Reply / public note written in PIO | PIO → PSA, provider first | PSA holds the thread | `TicketCommandService` (three paths: client comment, staff reply, system note) | Unchanged |
| Internal note written in PIO on a PSA ticket | PIO → PSA as an internal note, provider first | PSA | same, `IsPublic = false` | Unchanged |
| Note written in the PSA | PSA → PIO | PSA | Imported by the provider's note id; body, side and author healed on later reads | Unchanged |
| Note deleted in the PSA | PSA → PIO, for imported notes only | PSA | `ReconcileDeletedNotesAsync`. A note that **originated in PIO is never removed** because the PSA's copy went | Unchanged. This is the only inbound delete, and only of the PSA's own data |
| Note deleted in PIO | Not possible for PSA tickets | — | — | **No delete is propagated to a PSA** |
| Attachment uploaded in PIO | PIO → PSA, local first | Both hold a copy | `AttachmentService.PushToProviderAsync`; failure audited, file kept | Unchanged |
| Attachment added in the PSA | PSA → PIO, scanned before storage | PSA | Imported by attachment id | Unchanged |
| Attachment deleted in the PSA | PSA → PIO for imported files only | PSA | `ReconcileDeletionsAsync`; a portal upload is never removed | Unchanged |
| Time entry logged in PIO (clock or by hand) | PIO → PSA, local first | **PIO keeps its row; the PSA is the record for totals** | `TicketTimeWriter.PushAsync`; `Failed` entries retried from the ticket | Unchanged, plus: a retry after an uncertain failure must look for the entry in the PSA before creating another (see Duplicates) |
| Time entry changed or deleted in PIO | PIO → PSA, provider first | PSA | `TicketTimeController`; needs `tickets.time.log`, the author or a lead, and a reason when it is someone else's | Unchanged. This is the one outbound operation that deletes a record: a deliberate, permissioned, audited action on one entry, never a consequence of sync. (Changing a ticket's device in ConnectWise removes a link, not a record.) The full list of what each connector may call is in [PSA_CONNECTOR_REQUIRED_PERMISSIONS.md](PSA_CONNECTOR_REQUIRED_PERMISSIONS.md) |
| Time entry logged directly in the PSA | PSA → PIO **as totals and as an internal note only** | PSA | No `ticket_time_entries` row is created for it | Unchanged in Phase 9 (importing PSA-side entries as worklogs is deferred; see the audit) |

## Clients, contacts, technicians, configuration

| Data | Direction (as built) | Authority | Phase 9 rule |
|---|---|---|---|
| Client company | PSA → PIO | PSA | PSA → PIO. Never written to a PSA |
| Contact | PSA → PIO (read on demand; backfilled onto tickets) | PSA | PSA → PIO |
| Client portal login | PIO only | PIO | PIO only |
| Technician list | PSA → PIO, read on demand | PSA | **Mapping only.** A PSA resource never becomes a PIO user by itself, and a PIO user is never created in a PSA |
| PSA login ↔ PIO person (`user_psa_identities`) | PIO only | PIO | PIO only, per connection |
| Statuses, priorities, queues, categories, custom-field definitions | PSA → PIO, discovered | PSA | Read-only discovery |
| Mapping rules | PIO only | PIO | PIO only, per connection, versioned |
| Devices | PSA → PIO | PSA | PSA → PIO |
| Holidays, agreements | PSA → PIO, read on demand | PSA | PSA → PIO |

## PIO only: never synchronized

Working schedules, skills, capacity and time away, planned work (allocations), planning
requirements, work sessions and their segments, internal boards and their tickets, monitoring
alerts, knowledge base, recurring tickets, reports, analytics, insights, audit log, permissions.
None of it is sent to any PSA, and no connector has a method that could send it.

## Conflicts

A conflict needs two writers of the same field. With the rules above there are none that can
persist:

| Case | What happens | Why it is safe |
|---|---|---|
| Status changed in PIO and, independently, in the PSA | PIO's change is a call to the PSA. If the PSA already moved on, the PSA applies its own rules to the request and answers; PIO stores what the PSA accepted, and the next poll brings whatever the PSA holds | One writer of record (the PSA). PIO cannot hold a status the PSA does not |
| Assignment or queue changed on both sides | Same | Same |
| A ticket raised in PIO whose create failed, then created by hand in the PSA | Two tickets could exist. The failed one is marked `Error` and visible on the ticket and in integration health | Resolved by a person; resync is an explicit action |
| A time entry pushed, the answer lost, then retried | Could create a second entry in the PSA | **Open risk today.** Closed in Phase 9 by reconciling before a retry |
| The same ticket synced by the worker and by "Sync now" at once | Cannot happen: one run per connection, enforced by the database ([sync-engine.md](sync-engine.md)) | Was an open risk until Phase 9 slice 2a |

**Policy, per field:** `PSA_WINS` for every field in the ticket table marked PSA; `PIO_WINS` (never
sent) for everything in "PIO only". There is no field with `LATEST_VALID_CHANGE`, and none is
introduced. `MANUAL_REVIEW` applies to records, not fields: a record that could not be applied is
kept as a failed sync record for an authorized person to retry or dismiss.

A general-purpose conflict centre (two values side by side, "use PIO / use PSA") would only have
something to show if PIO kept local edits that the PSA had not accepted. The write-through rule
means it does not, so such a screen is **not required** by the model as it stands. It becomes
required the day an outbound queue lets PIO hold an unconfirmed value; that decision is recorded
in the audit and is not taken silently.

## Sync scope per connection (as built)

Open tickets and/or closed tickets; selected companies; selected queues or boards; selected
resources; active within N days; whether brand-new provider tickets are imported; whether notes,
system notes and attachments are mirrored; whether anything flows inbound at all (`TwoWaySync`).
The provider is asked to filter where it can, and the same filters are re-applied to what it
returns (`ConnectionSyncRunner.Passes`), so a provider that ignores a filter cannot widen the scope.
