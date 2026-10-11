# Phase 9 delivery tracker

What has been built for Phase 9 (the PSA connector framework), slice by slice: where it is, what
it changes in the database, how it was tested and what the tests said, what is known not to be
covered, and where each stands for merging. Written on 7 October 2026, before anything was merged; brought up to date on 11 October 2026, when the last slice was deployed.

Companion to the [audit](PHASE9_PSA_INTEGRATION_ARCHITECTURE_AUDIT.md) (what was found and why
each slice exists) and the [ownership matrix](PHASE9_SYNC_OWNERSHIP_MATRIX.md).

## Read this first

- **Every code slice of Phase 9 is merged and deployed.** Nineteen slices went into `main` one at
  a time between 7 and 11 October 2026, each on the owner's word, each deployed and checked before
  the next was handed over. Production runs `main` at `bab98e5`; its last migration is
  `20261007113506_OutboundQueue`.
- **Where the tests ran.** GitHub's CI ran on every slice's pull request and again on `main` after
  every merge: six checks each time, Firefox and Safari among them. Every one ended green. Two of
  them were red first and are described under "What CI found"; neither was a defect in the product.
  The unit, PostgreSQL 17 and Chromium figures in "Each slice" are from the development PC, as
  they were when each slice was built.
- **Every migration was rehearsed where it would run.** Before each of the eleven was deployed it
  was applied twice, from the generated script, to a copy of production restored from that day's
  backup in a throwaway PostgreSQL 17 container on the server.
- **No Phase 9 code has been certified against a real PSA.** Everything about Autotask and
  ConnectWise is held against stand-ins written for the tests. See "Certification against the real
  PSAs". Production's ConnectWise connection has synced on this code since 7 October; its Autotask
  connection has rejected its credentials since 23 September, so nothing here has run against
  Autotask at all. That is use, not certification.
- Figures are what the runs printed. Where a run failed and was repeated, that is said.

## The stack, as it was merged

Every branch name began `feat/psa-phase9-` and is deleted now that it is merged. Times are UTC.
"Deployed" is when the new containers started.

| # | Slice | Pull request | Merge commit | Merged | Deployed | Migration |
|---|---|---|---|---|---|---|
| 1 | Isolation and safety | #183 | `7042d30` | 7 Oct 13:02 | 7 Oct 13:08 | none |
| 2 | 2a. Sync engine | #184 | `3c959a5` | 7 Oct 13:39 | 7 Oct 13:44 | `PsaSyncEngine` |
| 3 | 2b. Provider calls | #185 | `145098b` | 7 Oct 15:45 | 7 Oct 15:52 | none |
| 4 | 3a. Connection lifecycle | #186 | `ad10c4d` | 8 Oct 12:17 | 8 Oct 12:28 | `ConnectionLifecycle` |
| 5 | 3b. Add-connection wizard | #187 | `88add73` | 8 Oct 13:45 | 8 Oct 13:50 | none |
| 6 | 3c. What belongs to a connection | #188 | `4413580` | 8 Oct 14:58 | 8 Oct 15:03 | none |
| 7 | 4a. Mapping health | #192 | `2dac6a6` | 9 Oct 07:26 | 9 Oct 07:31 | none |
| 8 | 4b. Mapping what a PSA sends | #193 | `6d77c18` | 9 Oct 10:55 | 9 Oct 10:59 | none |
| 9 | 4c. Technicians left alone | #194 | `07c5478` | 9 Oct 15:09 | 9 Oct 15:35 | `PsaTechnicianIgnores` |
| 10 | 7a. Certification and the new-connector guide | #195 | `529c7e4` | 9 Oct 16:33 | 9 Oct 16:37 | none |
| 11 | 7b. Performance at volume | #196 | `81b568d` | 11 Oct 09:15 | 11 Oct 09:20 | none |
| 12 | 6a. "Sync now" run by the worker; the job queue's claim | #197 | `b099902` | 11 Oct 09:52 | 11 Oct 09:56 | `SyncRequestsAndJobLease` |
| 13 | 4d. The PSA's classification, read | #198 | `323c843` | 11 Oct 10:27 | 11 Oct 10:32 | `TicketPsaClassification` |
| 14 | 7c. The three slow reads | #199 | `c4227ed` | 11 Oct 11:17 | 11 Oct 11:20 | `TicketListReads` |
| 15 | 4e. Classification mapping (decision 4) | #200 | `99bc98a` | 11 Oct 13:45 | 11 Oct 13:48 | `PsaClassificationMapping` |
| 16 | PSA time as worklogs (decision 3) | #201 | `0f7d7e3` | 11 Oct 14:23 | 11 Oct 14:27 | `ProviderWorklogs` |
| 17 | A client user with several companies (decision 1) | #202 | `d45e548` | 11 Oct 15:02 | 11 Oct 15:06 | `ClientCompanyAccess` |
| 18 | Custom fields (decision 5) | #203 | `c24acdf` | 11 Oct 15:39 | 11 Oct 15:44 | `PsaCustomFields` |
| 19 | Outbound queue (decision 2) | #204 | `bab98e5` | 11 Oct 16:20 | 11 Oct 16:24 | `OutboundQueue` |
| 20 | This tracker | the pull request this file is in | | | documents, and one test made stricter | none |

**Migrations, in the order they were applied** (eleven): `PsaSyncEngine`, `ConnectionLifecycle`,
`PsaTechnicianIgnores`, `SyncRequestsAndJobLease`, `TicketPsaClassification`, `TicketListReads`,
`PsaClassificationMapping`, `ProviderWorklogs`, `ClientCompanyAccess`, `PsaCustomFields`,
`OutboundQueue`. Production went from 80 tables to 88, with 151 tickets before and after every one.

Nine were additive only. The two that did more were checked harder, on the restored copy and
again in production afterwards:

- `TicketListReads` (14): the `pg_trgm` extension was created (version 1.6; production's role is
  allowed to), all 18 time entries were given their connection with none left empty and none
  differing from its ticket's, and the five indexes are present.
- `ProviderWorklogs` (16): production was read for duplicate PSA entry ids on the morning, again
  before the handover and again on the restored copy: 17 entries, 17 distinct ids. The unique
  index was created and is present.

No rollback was needed. A rollback tag was cut at `main` before every merge (`pre-psa-phase9-s1`
to `pre-psa-phase9-s19`) and a fresh backup taken before every handover.

## Each slice

What follows is each slice as it was built and measured before merging; the pull request and merge
commit are in each heading. "Suite" is the whole unit suite at that slice's head, run twice (the default build, and with the
build that uses real time zones as CI does), then the browser tests in Chromium, then the tests on
PostgreSQL 17 where the slice touched them. "New" counts tests added by the slice.

### 1. Isolation and safety — #183, merged as `7042d30`

- **Tests.** 116 new. Suite 1,389 in both modes; 16 on PostgreSQL 17.
- **Security.** Closes T1 (the scheduled sync loaded every organization's mapping rules), T2
  ("assigned to me" did not follow the PSA link; live in production), T3 in the rollup, T4, S1
  (a connection could be pointed at any address and the worker would call it), S2 (a webhook with
  no secret accepted anything), S5 in part, and D3, D9. T1, T2, T3, T4, S2 and D9 each have a test
  that fails on the code as audited.
- **Performance.** The ticket-visibility predicate, assembled by hand per connection, runs on
  PostgreSQL 17 and through a SQL translator; before this it had only run in memory.
- **Known limits.** No migration and no new screen. Visible effect in production: three linked
  technicians begin to see the PSA tickets assigned to them.

### 2. Sync engine — #184, merged as `3c959a5`

- **Tests.** 32 new. Suite 1,421 in both modes. On PostgreSQL 17, eight runs of one connection
  started at the same instant end with one.
- **Security.** Closes S3 (a sync could be started many times over): one run per connection,
  enforced by the database.
- **Performance.** Not measured here; see slice 11.
- **Known limits.** A large import now finishes over several runs and says "more to read"
  meanwhile. Closes D1, D2, D5, D6, D7, D10, D11; D1, D2, D5 and D6 were reproduced on the audited
  code first. See [sync-engine.md](sync-engine.md).

### 3. Provider calls — #185, merged as `145098b`

- **Tests.** 35 new. Suite 1,456 in both modes.
- **Security.** No finding of its own. The guard that refuses private addresses stays underneath
  the new HTTP layer, and is tested there.
- **Performance.** A sync of a large PSA paces itself (two requests a second once a burst is
  spent); a PSA that does not answer is given up on after a minute.
- **Known limits.** The pacing and the retry rules are set from each provider's published limits
  and held against stand-ins; how the real services answer is part of the blocked certification.
  The two connectors still map errors separately (R10 closed for the transport only). See
  [provider-calls.md](provider-calls.md).

### 4. Connection lifecycle — #186, merged as `ad10c4d`

- **Tests.** 37 new and 2 browser tests. Suite 1,493 in both modes; 72 browser tests.
- **Security.** Closes S4 (a new credential replaced the stored one before it was tested) and S5;
  the same PSA account cannot be connected twice (first half of T5).
- **Performance.** Not measured here.
- **Known limits.** See [connections.md](connections.md). The browser tests drive a stand-in PSA.

### 5. Add-connection wizard — #187, merged as `88add73`

- **Tests.** 13 new. Suite 1,506 in both modes; 73 browser tests, three of them the wizard, one
  from the first step to the last against a stand-in ConnectWise with the real connector making
  real HTTP calls.
- **Security.** Closes S6 (capability flags overstated). The connection test never writes to the
  PSA: in the browser run, no request other than a GET reaches the stand-in.
- **Performance.** Not measured here.
- **Known limits.** The end-to-end browser run is ConnectWise's; Autotask's wizard is covered by
  the unit tests and the catalog check, not by a browser run against a stand-in server.

### 6. What belongs to a connection — #188, merged as `4413580`

- **Tests.** 10 new. Suite 1,516 in both modes; 73 browser tests.
- **Security.** Closes T3 (people merged by a bare PSA id), the rest of T5 and T6. Five of the six
  places a person is counted have a test that fails on the old code.
- **Performance.** The pinned query budgets did not move.
- **Known limits.** Nothing changes for anyone until an organization has two accounts of one PSA.

### 7. Mapping health — #192, merged as `2dac6a6`

- **Tests.** 8 new. Suite 1,524 in both modes; 73 browser tests.
- **Security.** None of its own; the report is the connection's own organization's.
- **Performance.** Nothing stored; counted when asked. Measured in slice 11.
- **Known limits.** Closes R4. See [mapping-health.md](mapping-health.md).

### 8. Mapping what a PSA sends — #193, merged as `6d77c18`

- **Tests.** 7 new. Suite 1,531 in both modes; 73 browser tests.
- **Security.** Every change audited with what each value was mapped to before.
- **Performance.** Not measured here.
- **Known limits.** Statuses and priorities. Queues and categories keep the older rows of the page.

### 9. Technicians: linked, not linked, left alone — #194, merged as `07c5478`

- **Tests.** 3 new. Suite 1,534 in both modes; 73 browser tests.
- **Security.** None of its own.
- **Performance.** Not measured here.
- **Known limits.** A PSA technician never becomes a portal user by itself; that is by design.

### 10. Certification and the new-connector guide — #195, merged as `529c7e4`

- **Tests.** 11 new. Suite 1,545 in both modes. The contract suite a connector must pass ran at
  26 tests for each of the three connectors here (28 each after slice 18).
- **Security.** The suite holds a connector to "safe where a capability is not claimed".
- **Performance.** None.
- **Known limits.** **This certification is against stand-ins.** It says the connectors keep the
  contract; it does not say the real services behave as the stand-ins do. What is left of R7
  (wording that still branches on the provider in a few screens) is listed in
  [ADDING_NEW_PSA_CONNECTOR.md](ADDING_NEW_PSA_CONNECTOR.md).

### 11. Performance at volume — #196, merged as `81b568d`

- **Tests.** 7 new. Suite 1,552 in both modes; the volume tests and the benchmark on PostgreSQL 17.
- **Security.** None.
- **Performance.** Measured at 10 connections and 100,000 tickets, and at 100 connections,
  500,000 tickets and 1,000,000 time entries. Four things found and fixed: a sync held every
  ticket it had read (the same 5,000-ticket test: 9 min 1 s to 3 min 1 s); applying a mapping went
  client by client (965 queries to 49); the Connections page counted the tickets once per
  connection (14.4 s to about 0.12 s); a page of the ticket list read every ticket three times.
  All in [performance.md](performance.md).
- **Known limits.** Three reads were measured slow and left for slice 14. The benchmark is an
  administrator's view on this PC's disk, not production's hardware.

### 12. "Sync now" run by the worker; the job queue's claim — #197, merged as `b099902`

- **Tests.** 22 new. Suite 1,574 in both modes; 73 browser tests. On PostgreSQL 17, six workers
  taking the same thirty jobs take each once, with 150 saves refused.
- **Security.** None of its own.
- **Performance.** "Sync now" answers at once; the worker starts it within five seconds.
- **Known limits.** Closes R11. Provider-native webhooks, the rest of slice 6, are **not built**.

### 13. The PSA's classification, read — #198, merged as `323c843`

- **Tests.** 8 new. Suite 1,582 in both modes; 73 browser tests; 12 web unit tests.
- **Security.** The levels reach staff only; a client is not sent them.
- **Performance.** Not measured here.
- **Known limits.** A ticket already here gets its levels the next time it is read from the PSA.

### 14. The three slow reads — #199, merged as `c4227ed`

- **Tests.** 15 new. Suite 1,597 in both modes; 73 browser tests; volume tests and benchmark on
  PostgreSQL 17.
- **Security.** Nothing is cached, so nothing can be shown that the reader may no longer see.
- **Performance.** At 500,000 tickets: search 2.4 to 4.0 s became 28 to 140 ms; the dashboard
  summary 2.4 to 6.4 s became 0.35 to 0.62 s; the filter lists 2.6 to 4.9 s became 0.38 to 0.59 s.
  Five indexes, 93 MB at that size.
- **Known limits.** Still slow and not changed, listed in [performance.md](performance.md): a
  search of one or two letters or inside the conversation; the filter lists for someone who sees
  only their own tickets (not measured); no retention for sync runs or sync events (R9).

### 15. Classification mapping — #200, merged as `99bc98a`

- **Tests.** 15 new and 1 browser test. Suite 1,612 in both modes; 74 browser tests; 16 web unit;
  25 on PostgreSQL 17.
- **Security.** Another organization's connection is not found. The work type and subcategory
  reach staff only. Every change and every apply is audited.
- **Performance.** With 250,001 tickets on one connection: the page 4 queries in 255 ms, a
  preview 7 queries in 642 ms, applying to 83,333 tickets 119 queries in 5.7 s.
- **Known limits.** A classification no rule names is Unmapped and is given nothing; there is no
  catch-all rule, by the owner's decision. Skills are not derived from it. See
  [classification-mapping.md](classification-mapping.md).

### 16. PSA time as worklogs — #201, merged as `0f7d7e3`

- **Tests.** 12 new. Suite 1,624 in both modes; 74 browser tests; 27 on PostgreSQL 17.
- **Security.** Nothing read from the PSA is sent to it: the one method every send passes
  through refuses such a row.
- **Performance.** A ticket with two entries costs 12 database commands on first import and 10 on
  a re-read, whatever the number of tickets (PostgreSQL 17).
- **Known limits.** Whose time it is follows the PSA login's link; a login linked to nobody stays
  under the PSA's own name. Time already in the PSA arrives with the first "Re-sync all" after
  deploy, and figures for linked people rise by it. That a real PSA's entry ids are stable across
  edits is assumed and is part of the blocked certification. See [worklogs.md](worklogs.md).

### 17. A client user with several companies — #202, merged as `d45e548`

- **Tests.** 24 new and 2 browser tests. Suite 1,648 in both modes; 76 browser tests; 19 web unit.
- **Security.** This slice is a security change and is tested as attempts: a company not given,
  another organization's, one that does not exist, an unreadable id; a ticket asked for under the
  wrong company, both ways round; a look-only company asked to change something; a grant narrowed
  or removed between two requests; a forged row pointing at another organization. Two guards were
  removed in turn to see their tests fail. Access is decided on the server on every request and
  is never inferred from an e-mail address. Grants are made only by staff with `users.manage`.
- **Performance.** Not measured separately.
- **Known limits.** One company at a time; there is no combined view across companies. The
  browser suite cannot sign in as a client, so the hop from the company switcher's cookie to the
  API header is covered by unit tests on each side and not end to end. A weakness that was there
  before this slice (an approval answered by a matching e-mail address the user can edit) was
  found, is **not fixed here**, and is recorded as a separate task. See
  [client-company-access.md](client-company-access.md).

### 18. Custom fields — #203, merged as `c24acdf`

- **Tests.** 16 new and 1 browser test. Suite 1,664 in both modes; 77 browser tests; 19 web unit.
  The contract suite: 28 tests for each connector.
- **Security.** Staff only unless a field is separately confirmed, in words, as the client's to
  see. Who is shown a field is decided when the ticket is read. The tests serialise what a client
  is sent and search it for the field; they fail with the filter removed.
- **Performance.** Not measured separately; the imported-field list is read once per sync.
- **Known limits.** Read-only: no custom field is written to a PSA. The shape of each PSA's
  custom-field payload is taken from its documentation and held against stand-ins. See
  [custom-fields.md](custom-fields.md).

### 19. Outbound queue — #204, merged as `bab98e5`

- **Tests.** 22 new and 2 browser tests, and 1 on PostgreSQL 17. Suite 1,686 in both modes;
  79 browser tests; 19 web unit; 35 on PostgreSQL 17, where six workers
  taking the same thirty waiting changes take each once.
- **Security.** Retrying or letting go of a change needs the right to change that ticket and is
  reached only through that ticket. A client is sent none of it, and is not shown a staff reply
  the PSA has not got. The worker sends each change inside its own organization's scope. Six
  guards were removed in turn to see their tests fail (looking for a note before resending it;
  hiding an unsent reply from the client; the organization scope; refusing "Try now" while a
  worker holds the change; not re-creating a ticket in doubt; counting a try that broke).
- **Performance.** A change waits 30 s, then double each time up to an hour, eight tries. The
  worker asks every five seconds with one indexed query. Not measured at volume: a queue of many
  thousands of waiting changes was not built.
- **Known limits.** A status asked for while the PSA is away does not change the ticket until the
  PSA accepts it. Not queued: reassignment and queue moves, edits and deletions of time, device
  changes, attachments. A ticket that may already have been created is never created again by a
  machine. A request still waits for the PSA's answer, up to the connector's limit, before it is
  queued (R3 is narrowed, not closed). **One defect was found in review after the slice was
  first finished and fixed before it was committed:** the worker ran with every organization in
  view, so its audit entries would have belonged to no organization. See
  [outbound-queue.md](outbound-queue.md).

## The full run after everything was merged

Run on 11 October 2026 on `main` at `bab98e5`, the commit production runs:

| What | Result |
|---|---|
| Unit tests, default build | 1,686 passed, 0 failed |
| Unit tests, real time zones (as CI builds) | 1,686 passed, 0 failed |
| Model against migrations | no pending changes |
| PostgreSQL 17 (query budgets, volume, visibility, both claims) | 35 passed, 0 failed |
| Browser tests, Chromium | 79 passed, 0 failed, 0 flaky (6.4 min) |
| Web unit tests | 19 passed |
| CI on `main` at that commit (six checks, Firefox and Safari among them) | 6 of 6 green |
| The large benchmark (500,000 tickets) | not repeated; last run at slice 15, before merging |

## What CI found

CI ran on the 22 pull requests before this one (19 slices and 3 of side work), and on `main`
after each of the 20 merges. Two pull-request runs were red before they were green, both in the Firefox and Safari
job, and both for the same reason:
that job runs the browsers one after another against one server and one database, which the
Chromium job, alone in its own, does not.

- **#193 (slice 8).** The wizard's test counted every mapping rule in the organization and
  expected none. The second browser found the first one's two. The test now counts the rules of
  the connection it made, before the save (none) and after it (two). The product was not changed.
- **#190 (sign-in invitations, side work).** The invitation pages shared the public enquiry
  form's limit of five requests in ten minutes for one address; the second browser arrived with it
  spent and was shown "this link is not valid". This one did point at something real: a person
  reloading their link would have met the same wall. The link pages have their own limit now, and
  say "too many attempts" when it is reached.

**One test was made stricter after the last merge, by reading its own output.** The PostgreSQL
test for the outbound queue's claim printed "150 saves refused", but each worker read the row
afresh before claiming, so the others were turned away by that read and the version check was
never what stopped them. Each worker now keeps the rows as it first read them, as the job queue's
test always did, and the test fails when the version check is removed. The product was not
changed; what the test claimed and what it proved now agree. It is in the same pull request as
this update.

One more thing was learned about merging side work into a stack with migrations: a branch whose
migration was made before another migration reached `main` conflicts in the model snapshot. It is
rebased and its migration made again from `main`'s model, then rehearsed again. That was done for
#190 once.

## The full run before anything was merged

Run on 7 October 2026 on the `outbound-queue` branch, which contained every slice:

| What | Result |
|---|---|
| Unit tests, default build | 1,686 passed, 0 failed |
| Unit tests, real time zones (as CI builds) | 1,686 passed, 0 failed |
| Model against migrations | no pending changes |
| PostgreSQL 17 (query budgets, volume, visibility, both claims) | 35 passed, 0 failed |
| Browser tests, Chromium | 79 passed, 0 failed, 0 flaky (8.2 min) |
| Web unit tests | 19 passed |
| Firefox and Safari | not run then; run by CI on every slice since (below) |
| The large benchmark (500,000 tickets) | not repeated at this head; last run at slice 15 |

**About the browser runs on this PC.** Across the full Chromium runs made while building slices
15 to 19, two runs had one failing test, each time a test that has nothing to do with Phase 9
(`my-work` once, `satisfaction` once), each time when the run followed straight after a heavy
unit run. Each passed three times alone and in a repeat of the full run. They are recorded as
this machine under load, not explained further, and CI will be the judge as each branch gets its
pull request.

## Certification against the real PSAs

**Status: BLOCKED - TEST ENVIRONMENT REQUIRED, for both.** A real Datto Autotask sandbox and a
real ConnectWise PSA test environment are needed, with API credentials entered by the owner on
the server. Until then Phase 9 is not production-certified, whatever the stand-ins say.

"Against a stand-in" says only whether the behaviour is built and held by tests against the
fake Autotask and ConnectWise servers, or the stub connector, in this repository. It is not a
result for the real service, and a row that says "built, tested" is not a pass.

### Datto Autotask PSA

| Check | Real Autotask | Against a stand-in |
|---|---|---|
| Authentication | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Permissions | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested (the connection test's list) |
| Discovery | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Pagination | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested (every list read to its end) |
| Initial sync | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Incremental sync | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Ticket retrieval | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Status | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Priority | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Assignment | BLOCKED - TEST ENVIRONMENT REQUIRED | reading who a ticket is assigned to: built, tested. Reassigning from the portal was not changed in Phase 9 and is **not in the certification suite** |
| Notes | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Time entries | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Classification | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Custom fields | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Outbound writes | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Rate limits | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Retries | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Webhook | BLOCKED - TEST ENVIRONMENT REQUIRED | **not built** for Autotask's own callbacks (slice 6b). Only the portal's own signed frame exists, and is tested |
| Webhook replay | BLOCKED - TEST ENVIRONMENT REQUIRED | the portal's own frame refuses a replayed or stale delivery (tested); Autotask's own callbacks are **not built** |
| Idempotency | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Duplicate time-entry prevention | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Mapping | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Tenant isolation | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |

### ConnectWise PSA

| Check | Real ConnectWise | Against a stand-in |
|---|---|---|
| Authentication | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Permissions | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Company | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Contact | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Board | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Member | BLOCKED - TEST ENVIRONMENT REQUIRED | reading members, and who a ticket is assigned to: built, tested. Reassigning from the portal is **not in the certification suite** |
| Status | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Priority | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Type / Subtype / Item | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Tickets | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Notes | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Time entries | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Pagination | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Incremental sync | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Outbound writes | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Rate limits | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Retries | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Webhook | BLOCKED - TEST ENVIRONMENT REQUIRED | **not built** for ConnectWise's own callbacks. Only the portal's own signed frame exists, and is tested |
| Webhook replay | BLOCKED - TEST ENVIRONMENT REQUIRED | the portal's own frame refuses a replayed or stale delivery (tested); ConnectWise's own callbacks are **not built** |
| Idempotency | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Mapping | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |
| Tenant isolation | BLOCKED - TEST ENVIRONMENT REQUIRED | built, tested |

What the real environments are most needed to settle, because the stand-ins encode a reading of
the documentation and not an observation:

- how each service really answers when it is busy, slow or down (what a rate limit, a timeout and
  a server fault look like), which decides what the outbound queue treats as "certainly never sent";
- whether a note sent with its answer lost is found again by its words, in each service;
- the shape of custom-field values on a ticket, and of the three classification levels;
- whether a time entry's id survives an edit in the PSA;
- the permissions each API account really needs, against
  [PSA_CONNECTOR_REQUIRED_PERMISSIONS.md](PSA_CONNECTOR_REQUIRED_PERMISSIONS.md).

Production is connected to a real Autotask and a real ConnectWise today, on the code of Phase 8.
That is use, not certification, and none of the Phase 9 code has run there.

## Not built in Phase 9

- **Provider-native webhooks** (slice 6b). The portal reads both PSAs by polling. The webhook
  route was made safe in slice 1 (it refuses a connection with no secret) and remains a skeleton.
  Building it needs the test environments, to see what each service really sends.
- **Client mapping across PSAs** (R5): the same real client in two PSAs is still two clients.
- **Queueing of** reassignment and queue moves, edits and deletions of time, device changes and
  attachments.
- **Editable custom fields.** All are read-only.
- **A conflict screen.** Not required by the model as built; the reason is in the ownership matrix.
- **Still open from the audit's list:** R2 (connections are polled one after another), R8 (the
  health snapshot repeats organization-wide job counts on every connection), R9 (no retention for
  sync events or sync runs), the wording left of R7, and R3 narrowed but not closed.

## How the merging went

One at a time, in the order of the table, each on the owner's word:

1. The branch was rebased onto `main` and compared with itself before the rebase; apart from one
   test fix carried forward and one sentence shared with the rename, each was the same patch.
2. Its pull request was opened and all six checks waited for.
3. Where it had a migration: rehearsed on the server against a restored copy of production.
4. A fresh backup, a rollback tag at `main`, and the two commands handed to the owner.
5. After the deploy: the containers' age, the health endpoint, the error count in the API and the
   worker (0 after every deploy), the migration, and `main`'s CI.

Two slices did not deploy at the first attempt. Neither harmed production, which stayed on the
previous slice until the deploy went through:

- **Slice 3 (7 October).** The deploy was reported done before the merge had happened; the
  server showed no fetch and no new image. Once merged, the first deploy's session was cut while
  the images were building, with nothing swapped. Run again, it finished.
- **Slice 9 (9 October).** The deploy was reported done before the script had been run. The
  server showed the same, and the deploy was run.

Side work that went into `main` between slices, at the owner's request:

- **#189, the product is PioManage** (merged `9c2c23b`, 11 October). Every place a person could
  read the old name, the brand images included. Slice 15 was rebased over it.

Open beside the phase, built at the owner's request on 8 October and not part of it:

- **#190, sign-in invitations, password resets and the organization's e-mail wording.** Waits on a
  Keycloak client only the owner can create.
- **#191, extending a ticket's due date.** To be rebased when its turn comes.

## Waiting on the owner

- A Datto Autotask sandbox and a ConnectWise PSA test environment, for the certification above
  and for provider-native webhooks.
- New Autotask credentials in production: the connection has rejected its own since 23 September.
- The Keycloak client for #190, and the word for #190 and #191.
- Phase 10 is not begun, and is not begun without "BEGIN PHASE 10".
