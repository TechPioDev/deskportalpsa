# Mapping health

Phase 9, slice 4a. What a connection's PSA actually sends, set against the mapping rules there are:
what is unmapped, what the portal is doing with it meanwhile, and how to put it right.

The code is `ConnectionAdminService.Mapping.cs`; the tests are `MappingHealthTests`, a run through
a SQL translator in `RelationalQueryTests`, and the last browser test in `e2e/connections.spec.ts`.
The browser test checks the report and the sample and does not sync: in local mode the first sync
that imports tickets links the demo sign-in to a client, which would change who every later test
runs as. What applying does to imported tickets is held by the two unit suites.

## Why it exists

A value no rule maps **passes through as the PSA wrote it**. That looks exactly like a mapping that
let it through on purpose: the portal shows something plausible and nobody finds out. The portal
decides whether a ticket is finished from its status, and reads only the words "resolved" and
"closed" in it. So a PSA status called "Complete" that nothing maps counts as **open work**: it sits
in the open list, in the workload figures and in the SLA warnings. The sync logs each such value;
a log is not somewhere an administrator looks.

## Nothing is stored for it

There is no register of unmapped values to keep in step. A ticket keeps the status and priority it
**arrived with** (`PsaStatus`, `PsaPriority`) beside the portal's (`PortalStatus`,
`PortalPriority`), so what is unmapped is worked out, each time it is asked, from the tickets and
the rules as they are now:

- a value is looked up with the context the sync gives the mapping engine (connection, queue,
  client), so the answer here is the answer the sync would get;
- another connection's rule is not this connection's, even for the same value;
- a rule can map a value in one queue and not in another; the value is then shown as mapped with
  the tickets it does not cover counted separately.

It cannot drift from the data, and it survives a rule being changed, removed or rolled back without
anything having to be recalculated.

## What it reports

`GET /api/admin/connections/{id}/mapping-health` (`connections.manage`).

| Part | What it says |
|---|---|
| Statuses, Priorities | Every value tickets hold or the PSA lists: what it maps to, how many tickets hold it, how many of those nothing maps. For a status nothing maps: whether those tickets are counted as **open** or **finished** |
| Statuses set in the portal | Each status the portal can be set to, and what the PSA is sent for it. One with no counterpart, or mapped to something the PSA does not list, is said before someone tries to set it |
| Technicians | The PSA logins that hold this connection's tickets or that the PSA lists, and how many are linked to a portal user. Linking is optional. The account the portal itself writes as is nobody |
| Tickets without a client | Tickets under the "unknown" client, which the PSA sent without a company |

Where the PSA cannot be asked for its lists, the report is still given from what tickets hold, and
says that it is.

### Level

| Level | When |
|---|---|
| **Blocking** | No status is mapped at all. Finished work cannot be told from open work except by the PSA's wording |
| **Warning** | Tickets hold a status or priority nothing maps; or a status the portal can be set to has no counterpart in the PSA |
| **Optional** | Everything tickets hold is mapped. A value the PSA lists but no ticket holds is not, or a technician is not linked |
| **Pass** | Everything the PSA lists and everything tickets hold is mapped |

"Blocking" is a statement about what the figures can be trusted for. It does not stop the sync.

## Preview

`GET …/mapping-preview?take=10`. Sample tickets with what the rules make of each. The tickets most
recently synced where there are any; for a connection still being set up, a handful **read from the
PSA, mapped in memory and not kept**. The add-connection wizard shows this on its mapping step.

## Putting it right

1. Map the value on the Field Mapping page. The links in the report open that page on this
   connection and the right tab (`/dashboard/mappings?connection={id}&tab=status`).
2. **Apply the mapping to tickets already here** (`POST …/mapping-apply`). A new rule changes what
   the next sync makes of a ticket, and the sync reads only tickets that have changed, so tickets
   already imported would keep the PSA's word until they next changed. Applying re-maps them now.

What applying does and does not touch:

- Only a ticket whose portal value **is still the PSA's own word** is changed. A status someone set
  in the portal, or one a rule already translated, is not this rule's to rewrite.
- What the PSA said is kept as it said it. Only the portal's side changes.
- It works a group and a batch (500) at a time, so a connection of any size is done in bounded steps.
- It is audited as `mapping.applied`, with what changed to what and how many.
- Nothing is sent to the PSA.

## The portal's own words

The statuses and priorities a PSA's values are mapped *to* are listed once on the server, in
`PortalVocabulary`: `NEW`, `IN_PROGRESS`, `WAITING_CUSTOMER`, `ON_HOLD`, `RESOLVED`, `CLOSED`, and
`CRITICAL`, `HIGH`, `NORMAL`, `LOW`. The Field Mapping page in the browser still carries its own
copy of the same two lists; it reads the server's when that page is reworked (slice 4b).

## Not in this slice

Queues, categories and work types keep the PSA's names by design and are not scored. The mapping
page's own rework (search, filter to unmapped, counts, suggestions, bulk changes with a preview),
technician states and suggestions, client mapping and custom fields are slices 4b and 4c.
