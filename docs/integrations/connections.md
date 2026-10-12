# A PSA connection's life

Phase 9, slices 3a and 3b. What a connection is at each point between being added and being put
away, who may move it, and what the portal does and does not do with it meanwhile.

The rules live in `ConnectionAdminService` (changes; its `Setup` part holds the test, the preview
and the preflight), `ConnectionStates` (the one word for where it stands) and `SyncSchedule`
(whether the worker reads it). Each rule below has a test in `ConnectionLifecycleTests`,
`ConnectionSetupTests`, `AttentionTests` or `e2e/connections.spec.ts`.

## States

A connection's state is **worked out, not stored**: it comes from the connection's own fields and
whether a sync is running, so it cannot disagree with them. The order is the order of what a person
most needs to be told.

| State | When | Read from the PSA | Sent to the PSA |
|---|---|---|---|
| Archived | put away by an administrator | no | no |
| Setup | added, and no test has passed yet | no | no |
| Disabled | switched off | no | no |
| Syncing | a sync is running now | yes | yes |
| Credentials rejected | the PSA refused the stored credentials | **not automatically** | attempted, and refused by the PSA |
| Sync paused | reading paused by an administrator | no | yes |
| Error | the last test failed | **not automatically**, until a test passes | attempted |
| Degraded | the last sync failed; the next may not | yes | yes |
| Connected | accepted by the PSA, nothing synced yet | yes | yes |
| Healthy | the last sync succeeded | yes | yes |

Two of these deserve a sentence.

**Credentials rejected.** When the PSA answers "these credentials are wrong", the worker stops
polling that connection. Asking again every five minutes with a key the PSA has just refused is how
an API account gets locked. It is polled again as soon as someone saves credentials that pass a
test. The needs-attention list says this in words, once, as a critical item.

**Sync paused.** Only reading stops. A reply, a logged hour or a status change made in the portal
still goes to the PSA. When sync is resumed it starts from where it stopped: the cursor did not move
while it was paused, so what changed in the PSA meanwhile is inside the first question it asks. A
connection left paused for more than an hour appears in the needs-attention list as paused, not as a
sync that stopped for no reason.

## Adding a connection

A connection is added through a wizard of nine steps. It is saved, switched off, at the end of the
third, and switched on only by the ninth. Closing the wizard part-way leaves the connection in
setup; its card offers **Continue setup**, which opens the wizard at the test.

| Step | What happens | What the server does |
|---|---|---|
| 1. Choose PSA | A PSA with a connector is picked. Planned ones are named under "Coming soon" and cannot be | `GET /providers` |
| 2. Details | Name and API address | |
| 3. Credentials | The fields that PSA's connector asks for | Saved in setup: `IsEnabled` false, credentials to the secret store |
| 4. Test | The test, line by line (below). Nothing further until every needed line passes | `POST /{id}/check` |
| 5. Discover | The queues or boards, statuses, priorities, categories, work types and technicians the PSA lists | `GET /{id}/fields` |
| 6. Mapping | Each status and priority, and what it becomes in the portal, or **Not mapped** | `GET /{id}/mapping-coverage` |
| 7. Sync scope | Open and closed tickets, an age limit, and queues or boards ticked from the PSA's own list | `PUT /{id}/settings` |
| 8. Preview | How much that scope would bring in, and the preflight | `GET /{id}/preview`, `GET /{id}/preflight` |
| 9. Enable | Switched on, once any warning has been acknowledged | `POST /{id}/activate` |

There is no order of requests that gets a connection live without a passed test: `activate` refuses
a connection the PSA has not accepted or whose preflight has a failed line, and `enabled` refuses
one still in setup. A connection that fails its test stays in setup with the PSA's answer; nothing
is read or sent, and the needs-attention list carries one reminder ("not switched on yet"), not a
failed connection.

### The test

One call used to stand for all of it: a PSA account that could sign in and read nothing passed, was
switched on, and failed every sync. The test now tries each thing separately, and **only reads**.

| Line | Needed by the sync | How it is tried |
|---|---|---|
| Authentication | yes | The connector's own test call |
| Read tickets | yes | One ticket is asked for |
| Read statuses, priorities and queues | yes | The three lists are read |
| Read technicians | no | The list is read |
| Read ticket notes, Read time entries | no | On the ticket just read. With no ticket to ask about: **Not tried**, never "pass" |
| Log time | no | The PSA's own requirements for a time entry are read. Nothing is logged |
| Update tickets and add notes | no | **Not tried.** A test never changes anything in the PSA; the line says the connector can do it |
| Webhooks | no | What the connector says. Neither connector claims them until a PSA's own callbacks are understood |

When authentication fails nothing else is tried. When a needed read fails, a connection in setup
cannot be switched on. A **live** connection is not stopped by a failure that may not be there next
time (a timeout, a rate limit): the report says what happened, and the sync carries on.

### Mapping, scope and preview

- **Mapping.** A sample of tickets is read from the PSA and shown with what the rules would make
  of each; it is not kept. Once the connection is live its card has a **Mapping** panel with the
  full report ([mapping-health.md](mapping-health.md)). Values are looked up the way the sync looks
  them up, with this connection's rules only. A value no rule maps is shown as not mapped and counted. Nothing is guessed: it arrives as
  the PSA sends it until someone maps it. Unmapped values do not block; they are a warning the last
  step asks the administrator to acknowledge.
- **Scope.** Queues or boards are ticked from the list the PSA gave, not typed as ids. None ticked
  means all of them.
- **Preview.** Clients and technicians are counted from the PSA's lists; tickets are counted by
  asking the PSA for a count (`Tickets/query/count`, `service/tickets/count`) with the filter the
  sync itself would send, so no ticket is read to make the number. A figure the PSA cannot give is
  "not known", never zero. Contacts are read client by client during the sync and are not counted
  beforehand.

### Preflight

| Line | Fails when | Warns when |
|---|---|---|
| Connection | It has not passed a test | |
| Credentials | A field the connector asks for is not stored | |
| Mapping | | A status or priority the PSA lists has no mapping (they are named) |
| Sync scope | Neither open nor closed tickets are chosen; a chosen queue or board is not in the PSA's list | The PSA's list could not be read to check against |
| Conflicts | Another connection holds the same PSA account | Another connection has the same name |

The form's fields are not written into the page. Each connector describes itself: its name, an
example address, and the credential fields it needs with their labels and which are secret
(`GET /api/admin/connections/providers`). PSAs the portal names but has no connector for are in the
same list, marked unavailable (thirteen today, HaloPSA to DeskDay); the form shows them under
"Coming soon" and they cannot be chosen.
Creating a connection for one is refused by the API whatever the form does.

## Changing a connection

- **New credentials or a new address are tried before they are kept.** They used to be saved first:
  a mistyped key replaced a working one, and a changed address had the stored credentials sent to it
  before anything had checked it was the PSA. Now a refusal leaves the connection exactly as it was
  and says so ("Not saved: … The connection is unchanged and still uses what it had").
- Changing only the name or the logo asks nothing of the PSA.
- Saving the form does not switch a connection on or off. (The form used to send "enabled" every
  time, so correcting the name of a disabled connection enabled it.)
- A connection still in setup is the exception to the first rule: it has nothing working to protect,
  and is tested before it can be switched on in any case.

## One connection per PSA account

A second connection to the same account would import every ticket twice. The account is the host
plus the provider's own account name (Autotask: the API user; ConnectWise: the company id), kept as
a hash. Adding, editing or restoring a connection is refused when another connection that is not
archived already holds that account. A different account of the same PSA is a different connection
and is allowed. Connections made before accounts were recorded are recognised from their stored
credentials the first time the question is asked.

## Pause, disable, archive

| | Reads | Writes | Listed | Undone by |
|---|---|---|---|---|
| Pause sync | stop | continue | yes | Resume sync |
| Disable | stop | refused | yes | Enable (it is tested straight away) |
| Archive | stop | refused | no, in "Archived connections" | Restore (comes back switched off) |

Archiving removes nothing: tickets, clients, mappings, links and the stored credentials all stay, so
restoring is not setting up again. An archived connection is left out of the connection list, the
integration health snapshot, the organization's connection list and the needs-attention list. It no
longer holds its PSA account, so the same account may be connected afresh; restoring the old one is
then refused until the newer one is archived.

Every one of these is audited: `connection.activated`, `connection.sync.paused`,
`connection.sync.resumed`, `connection.enabled`, `connection.disabled`, `connection.archived`,
`connection.restored`.

## What the connection screen shows

- The state, in one word, with a sentence where the word is not enough.
- **Sync activity**: how far the sync has read ("everything changed in the PSA before … has been
  read"), whether a run is going or a long read is being continued, the last ten runs with who
  started each, and the records that could not be read, each with *Try on next sync* and
  *Stop trying*.
- **Manage**: pause or resume, disable or enable, archive.
- Archived connections, below the list, each with *Restore*.

## Routes

All under `/api/admin/connections`, all needing `connections.manage` unless noted.

| Route | Does |
|---|---|
| `GET /` (`connections.view`) | The connections that are not archived, each with its `state` |
| `GET /archived` | The ones put away |
| `GET /providers` | Every PSA the portal names, what each needs, and which have no connector |
| `GET /{id}/capabilities` | What this connection's PSA can do, asked of the connector |
| `POST /{id}/check` | The test, line by line. Reads only |
| `GET /{id}/mapping-coverage` | Each status and priority the PSA lists, and what this connection's rules make of it |
| `GET /{id}/preview` | How much an import would bring in under the connection's scope |
| `GET /{id}/preflight` | What stands between the connection and being switched on |
| `POST /{id}/activate` | Switches a connection on for the first time; refused until a test has passed and the preflight has no failed line |
| `POST /{id}/pause-sync`, `/resume-sync` | Stops and restarts reading |
| `POST /{id}/enabled` | Switches on or off; refused for a connection in setup or archived |
| `POST /{id}/archive`, `/restore` | Puts away and brings back |

## Database

One migration, `ConnectionLifecycle`, additive: five nullable-or-defaulted columns on
`psa_connections` (`InSetup` default false, `SyncPausedAt`, `ArchivedAt`, `LastErrorKind`,
`AccountKeyHash`). Existing connections are untouched by it: none is in setup, paused or archived,
and each one's account hash is filled the first time it is needed.

## What belongs to a connection (slice 3c)

Three things were known by less than the connection they belong to.

**A PSA login.** Someone with no portal account was identified by the PSA's id alone. Two PSA
accounts can each have a resource 42, and with two connections they were one person wherever
people are counted: one row in the team table, one day of hours, one satisfaction score, one name
in the ticket list, one line of portal coverage, and a filter that returned both people's tickets.
The key for such a person is now `x:{connection}:{id}` (`PersonKey`, built in one place). A portal
user is still `u:{user}`: one person across every PSA account. A key with no connection is one
written before this (an old saved view) and still means that login wherever it is found.

**A name.** Saved views and the ticket list's filters know a connection by its name, so a name
belongs to one connection. Adding, renaming or restoring a connection is refused when another that
is not archived has that name, whatever its case or spacing. Only a change of name is checked on an
edit, so two connections that already share one can still be edited (the preflight goes on saying
so).

**A ticket's reference.** "Autotask 12345" says which ticket only while there is one Autotask
account. Where an organization has two connections to the same PSA (archived ones count: their
tickets are still here), a ticket from one of them is referred to by its connection's name
("Customer A 12345") in work plans, My day, the planning queue and workforce analytics, and in the
notifications and audit entries about planned work. Audit entries about the work clock keep the
PSA's name beside the ticket's own id. With one account nothing changes. The connections are
read once for a request and only where a reference depends on one; a page of the team's own
tickets, which carry their own numbers, reads nothing extra.

## Not in these slices

A manual sync still runs inside the request that asks for it. It is bounded (50 pages a run, and
one run per connection), and moves to the job queue with the webhook slice, where the queue gets
its atomic claim. Limits by client or technician are still typed as ids under Sync settings. A
reference shown as a bare PSA id beside its client and title (satisfaction comments, the staff
report's oldest-open list) is unchanged: it claims no PSA, and the row it sits in says whose it is.
