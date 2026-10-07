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

**PIO → PSA (outbound).** A portal action that changes PSA data calls the provider **inside the
request**, and the pattern differs by kind of data. Until Phase 9 slice 5 that was all there was;
since then a change the PSA **could not be reached for** is kept and sent later
([outbound-queue.md](outbound-queue.md)). A change the PSA answers and refuses is refused at once,
as before.

| Pattern | Used for | What a provider failure does |
|---|---|---|
| **Provider first** (write-through) | Assignment, queue, device, time-entry change and delete | The request fails with the provider's message and **nothing is saved locally**. The portal never shows a value the PSA did not accept |
| **Provider first, kept as asked for when the PSA is away** | Status | A refusal fails the request as above. When the PSA cannot be reached the **ticket's status is not changed**; what was asked for is kept as a waiting change and becomes the status only when the PSA accepts it |
| **Provider first, the words kept when the PSA is away** | Replies and notes | A refusal fails the request as above. When the PSA cannot be reached the note is stored marked Pending Sync, shown to staff, **not shown to the client** until the PSA has it, and sent later |
| **Local first, then push** | A ticket raised in the client portal; a time entry logged in the portal; an uploaded attachment | The local record is kept and marked (ticket `SyncStatus = Error`; time entry `Failed`; attachment audited as `attachment.push_failed`) and can be pushed again. A time entry, and a ticket whose request certainly never left, are now retried by themselves |

**Echo suppression.** A portal write is recorded in `sync_events` with the hash of the state it
produced; when the same state comes back on the next poll the upsert recognises it and does nothing
(`TicketSyncService` → `IsPortalEchoAsync`). Notes, attachments and time entries are recognised by
the provider's own id, stored when the provider accepted them.

## Ticket fields

| Field | Direction (as built) | Authority (as built) | Source | Phase 9 rule |
|---|---|---|---|---|
| Title (subject) | PSA → PIO | PSA | `TicketSyncService` overwrites on every changed sync | Unchanged. Editing a PSA ticket's title in PIO is not offered |
| Description | PSA → PIO | PSA | same | Unchanged |
| Status | PSA ↔ PIO | **PSA**, write-through | In: mapped through the `status` rules. Out: `TicketStatusWriter` maps the portal value to the provider's and calls the provider first | Unchanged. PSA wins: a status set in PIO exists only once the PSA accepted it, so the two cannot diverge. An unmapped inbound value must be flagged, not guessed (see Mapping). **Asked for while the PSA is away, it waits as a change and the ticket keeps the status the PSA last gave it**; if the ticket's status has changed by the time it could be sent, it is not sent |
| Priority | PSA → PIO (set once outbound, when PIO creates the ticket) | PSA | In: `priority` rules. Out: only on create | Unchanged: PSA → PIO |
| Queue / board | PSA ↔ PIO | **PSA**, write-through | In: `queue` rules. Out: `TicketAssignmentController` (reassign) | Unchanged |
| Category (issue / type) | PSA → PIO (set once outbound on create) | PSA | In: `category` rules, raw kept in `PsaCategory`. A classification rule that gives a category speaks over them (slice 4e) | PSA → PIO. A rule's category is the portal's word and is never sent to the PSA |
| Classification (the PSA's three levels) | PSA → PIO | PSA | Kept as sent in `PsaTicketType`, `PsaIssueType`, `PsaSubIssueType`. Never written by the portal | PSA → PIO, read only |
| Work type and subcategory (the portal's) | PIO only | PIO, by the connection's classification rules | [classification-mapping.md](classification-mapping.md): from a rule, or empty. Never the PSA's word carried across | Not sent to the PSA. Staff only |
| Assigned technician (the PSA's resource / member) | PSA ↔ PIO | **PSA**, write-through | In: `AssignedTechnicianExternalId` + name. Out: reassign, with the role the PSA requires | Unchanged |
| Portal holder (`AssignedAppUserId`), handovers, followers | PIO only | PIO | Never sent | **PIO only.** Planning and "Take it" never change the PSA's assignee |
| Requester name and e-mail | PSA → PIO | PSA | Overwritten when the provider names a contact | Unchanged |
| Client company | PSA → PIO | PSA | Found or created by connection + external company id | PSA → PIO; creation becomes an explicit mapping decision (see Clients) |
| Created date (`PsaCreatedAt`) | PSA → PIO | PSA | Never defaulted to "now" | Unchanged |
| Resolved / closed dates | PSA → PIO | PSA | Overwritten from the provider | Unchanged |
| Due date / SLA due (`SlaDueAt`) | PSA → PIO | PSA | From the provider's own target | **PSA authoritative.** PIO never writes an SLA to a PSA |
| First-response promise, SLA pause | PIO only | PIO | Board and monitoring tickets only | PIO only |
| Device | PSA ↔ PIO | **PSA**, write-through | In: kept when the provider does not carry it. Out: a technician sets or changes it (`TicketDeviceService`, provider first); in ConnectWise the previous configuration is unlinked from the ticket, the configuration itself untouched | Unchanged |
| Time totals (worked, billable, non-billable) | PSA → PIO | PSA | Recomputed from the provider's entries on each sync of the ticket | Unchanged |
| Resolution text, resolved-by, reopen count, review state | PIO only | PIO | Never sent | PIO only |
| Portal custom fields, tasks, links, approvals, satisfaction | PIO only | PIO | Never sent | PIO only |
| A PSA's own custom fields on a ticket | PSA → PIO, **only the fields an administrator chose** for that connection | PSA | Kept as sent in `CustomFieldsJson`; a field not chosen is not stored. See [custom-fields.md](custom-fields.md) | PSA → PIO, **read-only**: never written to the PSA. Staff only unless a field is separately marked as the client's to see |

## Notes, attachments, time

| Data | Direction (as built) | Authority | Source | Phase 9 rule |
|---|---|---|---|---|
| Reply / public note written in PIO | PIO → PSA, provider first | PSA holds the thread | `TicketCommandService` (three paths: client comment, staff reply, system note) | Provider first. **When the PSA cannot be reached** a staff reply and a client's comment are kept, marked Pending Sync, and sent later; a staff reply is not shown to the client until the PSA has it. Before one is sent again after an unanswered send, the PSA's thread is read for it |
| Internal note written in PIO on a PSA ticket | PIO → PSA as an internal note, provider first | PSA | same, `IsPublic = false` | As the row above: kept and sent later when the PSA cannot be reached |
| Note written in the PSA | PSA → PIO | PSA | Imported by the provider's note id; body, side and author healed on later reads | Unchanged |
| Note deleted in the PSA | PSA → PIO, for imported notes only | PSA | `ReconcileDeletedNotesAsync`. A note that **originated in PIO is never removed** because the PSA's copy went | Unchanged. This is the only inbound delete, and only of the PSA's own data |
| Note deleted in PIO | Not possible for PSA tickets | — | — | **No delete is propagated to a PSA** |
| Attachment uploaded in PIO | PIO → PSA, local first | Both hold a copy | `AttachmentService.PushToProviderAsync`; failure audited, file kept | Unchanged |
| Attachment added in the PSA | PSA → PIO, scanned before storage | PSA | Imported by attachment id | Unchanged |
| Attachment deleted in the PSA | PSA → PIO for imported files only | PSA | `ReconcileDeletionsAsync`; a portal upload is never removed | Unchanged |
| Time entry logged in PIO (clock or by hand) | PIO → PSA, local first | **PIO keeps its row; the PSA is the record for totals** | `TicketTimeWriter.PushAsync`; `Failed` entries retried from the ticket | Unchanged, plus: a retry after an uncertain failure looks for the entry in the PSA before creating another, and an entry the PSA could not be reached for is Pending and retried by itself, where it was Failed until a person retried it |
| Time entry changed or deleted in PIO | PIO → PSA, provider first | PSA | `TicketTimeController`; needs `tickets.time.log`, the author or a lead, and a reason when it is someone else's | Unchanged. This is the one outbound operation that deletes a record: a deliberate, permissioned, audited action on one entry, never a consequence of sync. (Changing a ticket's device in ConnectWise removes a link, not a record.) The full list of what each connector may call is in [PSA_CONNECTOR_REQUIRED_PERMISSIONS.md](PSA_CONNECTOR_REQUIRED_PERMISSIONS.md) |
| Time entry logged directly in the PSA | PSA → PIO, as totals, as an internal note **and as a worklog** | PSA | A `ticket_time_entries` row with `Source = Provider`, known by the connection and the PSA's entry id; rewritten when the PSA's entry changes and removed when it is deleted there. See [worklogs.md](worklogs.md) | PSA → PIO only. **Never sent to the PSA**: `TicketTimeWriter.PushAsync` refuses a row read from it |

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
| A time entry pushed, the answer lost, then retried | The PSA is asked for the entry first; one that is there is adopted | Was an open risk; closed in Phase 9 slice 2b, and the same check runs for a queued retry |
| A reply sent, the answer lost, then retried | The PSA's thread is read first; the note that is already there is adopted and nothing is sent | Slice 5. Held against a stand-in; how a real PSA behaves is part of the blocked certification |
| A status asked for while the PSA was away, and the ticket's status changed before it could be sent | The waiting change is failed with the reason and **not sent**; the ticket keeps what it has | The PSA's status is not overwritten by what somebody wanted earlier. A person can ask again |
| A ticket raised while the PSA was away, the answer lost on a later try | Not retried by a machine: stored, marked in error, listed, for a person to look in the PSA and resend | There is no way to ask a PSA whether it created a ticket. Only a request that certainly never left is retried automatically |
| The same ticket synced by the worker and by "Sync now" at once | Cannot happen: one run per connection, enforced by the database ([sync-engine.md](sync-engine.md)) | Was an open risk until Phase 9 slice 2a |

**Policy, per field:** `PSA_WINS` for every field in the ticket table marked PSA; `PIO_WINS` (never
sent) for everything in "PIO only". There is no field with `LATEST_VALID_CHANGE`, and none is
introduced. `MANUAL_REVIEW` applies to records, not fields: a record that could not be applied is
kept as a failed sync record for an authorized person to retry or dismiss.

A general-purpose conflict centre (two values side by side, "use PIO / use PSA") would only have
something to show if PIO kept local edits that the PSA had not accepted. The write-through rule
means it does not, so such a screen is **not required** by the model as it stands.

The outbound queue (slice 5) was built so that this stays true. It holds two kinds of thing, and
neither is a PSA-owned field with a local value the PSA has not accepted: words somebody wrote (a
note, which the PSA holds a copy of and does not author), and a request to change a status, kept
**as a request** while the ticket goes on showing the PSA's value. A waiting or failed change is
listed on its ticket and under Unsent changes, which is where a person decides about it. If a
later change lets PIO hold an unconfirmed value of a PSA-owned field, a conflict screen becomes
required; that decision is to be recorded in the audit and not taken silently.

## Sync scope per connection (as built)

Open tickets and/or closed tickets; selected companies; selected queues or boards; selected
resources; active within N days; whether brand-new provider tickets are imported; whether notes,
system notes and attachments are mirrored; whether anything flows inbound at all (`TwoWaySync`).
The provider is asked to filter where it can, and the same filters are re-applied to what it
returns (`ConnectionSyncRunner.Passes`), so a provider that ignores a filter cannot widen the scope.
