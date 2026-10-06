# A PSA connection's life

Phase 9, slice 3a. What a connection is at each point between being added and being put away, who
may move it, and what the portal does and does not do with it meanwhile.

The rules live in `ConnectionAdminService` (changes), `ConnectionStates` (the one word for where it
stands) and `SyncSchedule` (whether the worker reads it). Each rule below has a test in
`ConnectionLifecycleTests`, `AttentionTests` or `e2e/connections.spec.ts`.

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

1. **Saved switched off, in setup.** It used to be enabled the moment it was saved, before its
   credentials had been tried once.
2. **Tested.** The screen tests it straight away. A connection still in setup is tested with what
   was just saved, whether or not it is switched on.
3. **Switched on only if the test passed.** `POST …/activate` refuses a connection the PSA has not
   accepted, and `POST …/enabled` refuses one still in setup, so there is no order of requests that
   gets a connection live without a passed test.

If the test fails, the connection stays in setup with the PSA's answer on its card. Nothing is read
or sent. It shows in the needs-attention list as one reminder ("not switched on yet"), not as a
failed connection.

The form's fields are not written into the page. Each connector describes itself: its name, an
example address, and the credential fields it needs with their labels and which are secret
(`GET /api/admin/connections/providers`). PSAs the portal names but has no connector for are in the
same list, marked unavailable; the form shows them under "Coming soon" and they cannot be chosen.
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
| `POST /{id}/activate` | Switches a connection on for the first time; refused until a test has passed |
| `POST /{id}/pause-sync`, `/resume-sync` | Stops and restarts reading |
| `POST /{id}/enabled` | Switches on or off; refused for a connection in setup or archived |
| `POST /{id}/archive`, `/restore` | Puts away and brings back |

## Database

One migration, `ConnectionLifecycle`, additive: five nullable-or-defaulted columns on
`psa_connections` (`InSetup` default false, `SyncPausedAt`, `ArchivedAt`, `LastErrorKind`,
`AccountKeyHash`). Existing connections are untouched by it: none is in setup, paused or archived,
and each one's account hash is filled the first time it is needed.

## Not in this slice

The step-by-step wizard (test matrix, discovery, scope chosen from lists, preview, preflight) and a
manual sync that runs in the background are slice 3b. Keys that still ignore the connection (people
without a portal account, saved views and filters matched on names, references shown without their
connection) are slice 3c.
