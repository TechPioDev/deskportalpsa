# Mapping health, and mapping what a PSA sends

Phase 9, slices 4a and 4b. What a connection's PSA actually sends, set against the mapping rules
there are: what is unmapped, what the portal is doing with it meanwhile, and how to put it right.

The code is `ConnectionAdminService.Mapping.cs` (the report, the preview, applying) and
`MappingAdminService.SetInboundAsync` (saying what values become); the tests are
`MappingHealthTests`, `MappingInboundTests`, a run through a SQL translator in
`RelationalQueryTests`, and the last browser test in `e2e/connections.spec.ts`.
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

## Two directions, two kinds of rule

A PSA has many statuses and the portal has six. So the mapping has two halves that are not mirror
images:

| | Rule | How many |
|---|---|---|
| What the portal **sends** for one of its own statuses | portal value → the PSA's value, direction *portal to provider* or *both ways* | one per portal value |
| What a PSA value **arrives** as | the PSA's value → portal value, direction *provider to portal* (or the same *both ways* rule) | any number per portal value |

The Field Mapping page used to save only the first kind, and the API found a rule by its portal
value, so a second PSA status mapped to "in progress" overwrote the first. Production has inbound
rules for its other PSA statuses and priorities (19 on 6 Oct, counted in the database); they were not made on that page, which
could not make them.

`PUT /api/admin/mappings/inbound/{connectionId}` (`mappings.manage`) takes any number of
`{ field, value, portalValue }`:

- any number of PSA values may become one portal value; each is its own inbound rule;
- where one rule works both ways and only what **arrives** is being changed, the rule keeps its
  sending half (it becomes *portal to provider*) and a new inbound rule says what arrives. What the
  portal sends for its own value is never changed from here;
- a null `portalValue` takes the mapping away: the value then arrives as the PSA wrote it;
- two rules that both decided what one value arrives as (possible before) are made one;
- only this connection's own rules are touched;
- every line is checked before any is saved, and every problem is said at once. A portal value has
  to be one of the portal's own (`GET /api/admin/mappings/vocabulary`);
- the set is saved together as **one** version, and audited as `mapping.inbound.changed` with the
  connection and, for each value, what it was mapped to before and what it is mapped to now.

## Putting it right

1. Map the value on the Field Mapping page, in **What the PSA sends**. The links in the report open
   that page on this connection and the right tab
   (`/dashboard/mappings?connection={id}&tab=status`). The section lists every value the PSA sends
   or lists, with the tickets that hold it, and offers:
   - a search, and a filter to what is unmapped;
   - how many are mapped and how many tickets hold an unmapped value;
   - **Suggest exact matches**: an unmapped value that is the same words as a portal value
     ("In Progress" and `IN_PROGRESS`). Nothing looser is ever suggested;
   - ticking several values and mapping them to one portal value.

   Every choice, one at a time, ticked or suggested, is **staged** and shown as a list of changes
   with what each value was mapped to before. Nothing is saved until that list is saved.
2. **Apply the mapping to tickets already here** (`POST …/mapping-apply`). A new rule changes what
   the next sync makes of a ticket, and the sync reads only tickets that have changed, so tickets
   already imported would keep the PSA's word until they next changed. Applying re-maps them now.

What applying does and does not touch:

- Only a ticket whose portal value **is still the PSA's own word** is changed. A status someone set
  in the portal, or one a rule already translated, is not this rule's to rewrite.
- What the PSA said is kept as it said it. Only the portal's side changes.
- It works a value and a batch (500) at a time, so a connection of any size is done in bounded
  steps and no more than a batch is held in memory. Only where a rule of the field is written for
  one client or one board are that field's tickets taken a client and a board at a time: the same
  word can then mean two things on one connection. See [performance.md](performance.md).
- It is audited as `mapping.applied`, with what changed to what and how many.
- Nothing is sent to the PSA.

## The portal's own words

The statuses and priorities a PSA's values are mapped *to* are listed once on the server, in
`PortalVocabulary`: `NEW`, `IN_PROGRESS`, `WAITING_CUSTOMER`, `ON_HOLD`, `RESOLVED`, `CLOSED`, and
`CRITICAL`, `HIGH`, `NORMAL`, `LOW`. "What the PSA sends" reads them from the server. The older
rows on the Field Mapping page (what the portal sends) still carry their own copy of the same two
lists.

## Technicians: linked, not linked, left alone

A PSA login is one of three things on a connection.

| State | What it means | Where it is set |
|---|---|---|
| **Linked** | This login is this portal user. Its tickets and time in the PSA count as theirs | A user's page, under PSA identity; or Users, Import from PSA |
| **Not linked** | Shown under the PSA's own name. Still to do, or never needed | |
| **Left alone** | An administrator has said it is nobody the portal needs to know as one of its people: an API or service account, someone who left | Import from PSA, or the Mapping panel's technician list |

Before this there were two, and "not linked" read as something still to do: an API account sat in
the list for good, and the count of technicians to link stopped meaning anything.

- A suggestion is made only where the PSA's e-mail for the login is **exactly** a portal user's
  (Import from PSA shows it as *Link*, and nothing is linked until that is pressed). Names are
  never matched.
- No portal user is ever created without an administrator choosing *Add* for that one technician,
  and a login with no e-mail in the PSA cannot be added at all.
- Leaving a login alone changes nothing about its tickets or time: they are shown under the PSA's
  own name, as for any login that is not linked.
- A login that is linked cannot be left alone, and linking a login that was left alone takes that
  decision back. It is one or the other.
- A login belongs to one connection: left alone on one PSA account is not left alone on another.
- Each decision is audited (`psa.technician.ignored`, `psa.technician.unignored`) with the
  connection and the login.

`PUT /api/admin/psa-technicians/{connectionId}/{login}/ignored` (`users.manage`), body
`{ "ignored": true, "name": "…" }`. One table, `psa_technician_ignores`, one row per login left
alone (migration `PsaTechnicianIgnores`, additive).

## Not in these slices

Queues, categories and work types keep the PSA's names by design and are not scored. Client
mapping waits on a decision (may one client login reach two companies?); work types' lower levels
and custom fields are still to build.
