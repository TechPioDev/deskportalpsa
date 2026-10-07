# Phase 9: PSA integration architecture audit

**Status:** written before any Phase 9 code, from the repository at `main` `9786eec` and a
read-only look at production on 5 Oct 2026. Every statement about the code names the file it comes
from; statements marked *(read, not run)* were confirmed by reading the code path and have not been
reproduced by a test.

**Verdict.** The integration is built on the right shape already: one connector contract, a
capability record, a uniform error model, connection-scoped identity for tickets and companies,
mapping rules with history, and encrypted credentials. Phase 9 is therefore an **extension**, not a
rewrite. The audit also found **six high-severity defects in what exists**: one security, three
data-loss and two isolation defects. Five are latent at today's size (one organization, two
connections, 151 tickets). **One is live now**: technicians limited to "assigned to me" do not get
the tickets their PSA assigned to them through their PSA link (T2). None of the six makes
implementation unsafe; all of them have to be closed **before** the framework is widened, so they
are the first two slices of work.

Companion: [PHASE9_SYNC_OWNERSHIP_MATRIX.md](PHASE9_SYNC_OWNERSHIP_MATRIX.md).

---

## 1. Previous phases

| Check | Result |
|---|---|
| Phases 1 to 8 | Live. Phase 8 merged as `9786eec`, deployed 5 Oct 13:36 UTC, verified |
| Main CI on `9786eec` | 6 of 6 green, including Firefox and Safari |
| Unit tests | Green in main's CI. Baseline before the first code change: 1,273 passed, 0 failed |
| Production | 80 tables, 0 errors since the last start |

## 2. Production as it stands (read-only, 5 Oct 2026)

| Fact | Value |
|---|---|
| Organizations with connections | 1 |
| Connections | 2: **Autotask** (production zone `webservices31.autotask.net`; Degraded, last success 23 Sep, an error recorded) and **CWM** (ConnectWise, `staging.connectwisedev.com`; Healthy, syncing) |
| Tickets | Autotask 137 · ConnectWise 13 · 1 internal |
| Client companies | Autotask 7 · ConnectWise 4. No placeholder companies |
| Mapping rules | 52 (50 active), **all scoped to their connection**: status 26, priority 13, queue 9, category 2. No value is mapped two ways in either direction. 33 saved versions |
| PSA logins linked to people | Autotask 2 of 2 · ConnectWise 1 of 1 (3 people; nobody is linked on both) |
| Staff | 41 active. **40 see only tickets "assigned to me"**, including all 3 linked people; 1 sees all |
| Legacy technician id on the person (`app_users.ExternalTechnicianId`) | Set for **0 of 41** |
| PSA tickets with a PSA assignee | 40. With a portal holder as well: 0 |
| Notes | 363 (340 imported from a PSA) |
| Attachments | 37 (Autotask 25 imported + 2 pushed; ConnectWise 7 + 3) |
| Portal time entries | 17: 16 in the PSA with its id, 1 failed push |
| Background jobs | 0 rows: the job queue has never been used in production |
| Sync events | 42, none marked processed (nothing ever marks one) |
| Import scope on both connections | Open and closed tickets, active within 7 days, no company / queue / resource filter |
| Duplicates | None: no repeated (connection, external id) for tickets or companies; no repeated note or time-entry id on a ticket; no external ticket id shared by two connections |

## 3. Current connector architecture

```
PIO services ── IConnectorResolver.ResolveAsync(connectionId)
                    │   loads the connection under the caller's tenant; refuses a disabled one
                    ▼
            IConnectorFactory (one per provider, scoped)
                    │   reads the endpoint from the row and the credentials from the secret store
                    ▼
            IServiceManagementConnector   (packages/psa-core: 35 members)
              ├── AutotaskConnector     (packages/connectors/Autotask, 1,105 lines)
              ├── ConnectWiseConnector  (packages/connectors/ConnectWise, 948 lines)
              └── MockConnector         (tests only; its factory is not registered)
```

- **Contract** (`IServiceManagementConnector`): capabilities, test, organizations, contacts,
  technicians and their queue coverage, devices, agreements, holidays, tickets (page / one /
  create / update), notes, attachments (list / add / dated sweep / download), time entries (list /
  add / update / delete), discovery lists (statuses, priorities, queues or boards, categories, work
  types, work roles, custom fields), webhook validate and normalise.
- **Unified models** (`UnifiedModels.cs`): `UnifiedTicket`, notes, attachments, time entries,
  `ExternalFieldOption` with **Value** (what the provider is sent) and **SyncValue** (what a synced
  ticket carries), `TicketFilter` (modified since, page size, cursor, company / queue / resource
  ids, include closed, active within days).
- **Capabilities** (`ProviderCapabilities`): 25 flags plus maximum page size, maximum attachment
  size, a rate-limit model name and the authentication types.
- **Errors** (`ConnectorException`): Authentication, PermissionDenied, RateLimited (with
  `RetryAfter`), Timeout, NotFound, InvalidRequest, ProviderError; `IsTransient` for the last-named
  three kinds of trouble (Timeout, ProviderError, RateLimited).
- **Resolver** (`ConnectorResolver`): no cache; each resolve is two reads of the connection row, one
  secret read, a new `HttpClient` and a new connector. A provider with no factory is a validation
  error. 15 providers are named in `ProviderType`; **2 have a connector**.

**Neither real connector throws "not supported" anywhere.** Two members return empty by design in
ConnectWise (the dated attachment sweep, custom fields).

## 4. The two connectors

| | Autotask | ConnectWise |
|---|---|---|
| Authentication | Three headers (`ApiIntegrationCode`, `UserName`, `Secret`) on every request | Basic (`company+public:private`) and a `clientId` header |
| Credentials in a URL, a log line or an exception | No | No |
| Base URL | Per connection, typed by the administrator (no zone discovery) | Per connection |
| Ticket paging | `nextPageUrl`, re-posted; the URL must be on the configured host and scheme | Page number, ordered by id |
| Every other list | **One page** (500; a few at 50) | **One page** (1,000; a few at 10, 50, 100) |
| Incremental filter | `lastActivityDate >=` | `lastUpdated >` (to the second) |
| 429 | Becomes `RateLimited` with `Retry-After` (seconds form only; 10 s otherwise) | Same |
| Throttling, retries | None | None (one `SemaphoreSlim(4)` around per-ticket device reads) |
| Timeout | None set (the framework's 100 s) | None set |
| Writes | Bare POST / PATCH / DELETE. The idempotency key on the request models is **ignored** | Same |
| Status, priority, queue, category on a ticket | The **label**; the id is not kept beside it | The **name**; the id is dropped |
| "Category" | The `ticketCategory` picklist. Issue type and sub-issue type are write-only, on create | The ticket **type** (discovery reads the types of the first board only). Subtype and item are not read |
| Custom fields | Names only; values are never read or written | Empty, although the capability says supported |
| Company on a ticket | `RequesterExternalId` holds the **company** id; the contact id is not kept | Same |
| SLA target | `resolvedDueDateTime ?? dueDateTime` | `requiredDate ?? resolutionGoalUTC` |

**Duplicated between the two** (and a third time in the mock where marked): webhook validation and
normalisation and the HMAC helper (×3); the HTTP status to `ConnectorException` switch; error-body
extraction; the transport try/catch (once in Autotask, four copies in ConnectWise, which has four
send paths and builds its auth header three times); get-by-id with not-found as null; label
matching; the factories (load connection, read secret, require keys, trailing slash, create client).

## 5. Current database schema (integration tables)

| Table | Identity of the external record | Unique index (production) | Two connections of one provider with the same external id |
|---|---|---|---|
| `psa_connections` | `Id` (the stable connection id) | Primary key | Any number of connections per provider per tenant |
| `tickets` | `PsaConnectionId` + `ExternalTicketId` (+ `Provider`) | **Unique** (`PsaConnectionId`, `ExternalTicketId`) where the id is set | **Safe** |
| `client_companies` | `PsaConnectionId` + `ExternalCompanyId` | **Unique** (`PsaConnectionId`, `ExternalCompanyId`) | **Safe**, and therefore one client row **per connection**: the same real company in two PSAs is two clients |
| `client_users` | `ExternalContactId` under a company | Unique (`ClientCompanyId`, `Email`) only | Safe through the company |
| `ticket_notes` | `TicketId` + `ExternalNoteId` | **None** | Safe through the ticket; **no protection against a double import** |
| `ticket_attachments` | `TicketId` + `ExternalAttachmentId` | **None** | Same |
| `ticket_time_entries` | `TicketId` + `ExternalEntryId` | Non-unique index on `ExternalEntryId` alone | Lookups are by ticket, so safe; **no protection against a double import** |
| `devices` | `PsaConnectionId` + `ExternalId` | Non-unique | Safe by lookup |
| `user_psa_identities` | `PsaConnectionId` + `ExternalTechnicianId` | Unique (`AppUserId`, `PsaConnectionId`) | Safe. **Two people can be linked to the same PSA login** on one connection |
| `app_users.ExternalTechnicianId` | One technician id per person, **no connection** | None | **Not safe**: the column ticket visibility reads (T2). Written by nothing |
| `activity_daily_facts` | `ActorExternalId`, **no connection** | Non-unique | **Not safe**: two logins with one id are one actor (T3) |
| `psa_connections` | provider + endpoint + tenant identifier | None | The same PSA account can be added twice; two connections can share a name |
| `field_mappings` | scope + connection + field + value | None | Safe **only** for rules at connection scope (T1) |
| `sync_events` | `PsaConnectionId` + `IdempotencyKey` | **Unique** | Safe |
| `background_jobs` | `Id` | — | Tenant-scoped, not connection-scoped |
| `secret_blobs` | opaque reference | Primary key | — |

The identity the brief asks for (**tenant + connection + object type + external id**) is what
tickets and companies already use. There is **no generic external-reference table**: each entity
carries its own columns.

## 6. Current synchronization architecture

**Inbound (polling only).** `PollingSyncService` (worker) every `Sync:PollIntervalMinutes` (5)
walks every enabled connection that is not `Failed`, **one after another, across all tenants**, each
in its own scope. `ConnectionSyncRunner.RunAsync` then, for one connection:

1. resolves the connector and reads its capabilities;
2. loads the mapping rules of the connection's **provider**;
3. pages tickets changed since `LastSuccessfulSyncAt` (page size 100, **at most 50 pages**),
   re-applies the connection's filters to each ticket, and upserts it by connection + external id
   (`TicketSyncService`: unchanged-hash short-circuit, portal-echo short-circuit, apply);
4. for **each ticket**: reads its notes, resolves its assignee's name, reads its time entries
   (totals and time-entry notes);
5. sweeps attachments (a dated tenant-wide query where the provider has one, else per touched
   ticket), scanning every file before storing it;
6. stamps `LastSuccessfulSyncAt = now`, `Status = Healthy`.

Any exception from steps 1 to 3 marks the connection `Degraded` with the message and rethrows; the
cursor is not moved. An error on a ticket's notes, time entries or files is swallowed.

**Outbound.** No queue. Eleven call sites call the connector inside the request (two of them
directly from controllers). Two patterns, set out in the ownership matrix: *provider first* for
status, assignment, queue, notes and time changes; *local first, then push* for a client-raised
ticket, a logged time entry and an uploaded attachment.

**Manual.** `POST api/admin/connections/{id}/sync?full=` runs the same runner **inside the HTTP
request**.

**Sync state.** One timestamp per connection (`LastSuccessfulSyncAt`). No per-entity cursor, no
record of a run (when it started, how long, how many, what failed), no lock.

## 7. Current mapping architecture

- `FieldMapping`: provider, **scope** (platform default, provider default, connection, client
  company, queue or board, ticket type, custom field, conditional), connection, client, queue key,
  ticket-type key, portal field and value, external field and value, direction, required, fallback,
  condition JSON, active, version. `FieldMappingVersion` keeps snapshots per provider + connection.
- `MappingEngine` (pure, 97 lines): narrowest scope wins; a value-specific rule beats a field-level
  one. A **miss** is reported to the caller. Only a rule at **connection** scope is tied to its
  connection: platform, provider, custom-field and conditional rules apply to every connection of
  the provider, and queue and ticket-type rules match on the key alone.
- Rules are **loaded by provider**, not by connection, in all six places that map a value (the sync
  runner, resync, replies, status, both assignment routes) and in the admin API, which is keyed by
  provider with the connection optional.
- Fields in use: `status`, `priority`, `category`, `queue` (and work type in the admin page).
- **A miss passes the provider's raw value through** into the portal column
  (`TicketSyncService.Map`), with one log warning per value per run. Nothing is stored that says
  "unmapped"; Phase 8's health page infers it by comparing against the normalized sets.
- `MappingAdminService`: upsert, delete, versions, rollback, snapshot, each audited. The web page
  lists rules **per provider** and filters by connection in the browser.
- Technicians: `user_psa_identities` (one PSA login per person per connection), set by hand or by
  provisioning a portal user from a PSA technician (`users.manage`). No "ignored" state, no
  suggestion.
- Clients: **no mapping**. `EnsureCompanyAsync` silently creates a client the first time a ticket
  names a company the connection has not seen.
- Custom fields, work type and subtype, time-entry type, ticket source: not mapped.

## 8. Current webhook architecture

`POST api/webhooks/{connectionId}` (anonymous by design): loads the enabled connection, asks the
connector to validate (HMAC-SHA256 of the **body** against `X-Signature`, `X-Timestamp` within five
minutes), normalises to an event, registers it in `sync_events` (duplicates acknowledged, not
re-processed), queues a `sync.inbound-event` job and answers 202.

It is a **skeleton**:

- the job's handler only writes a log line (`InboundEventJobHandler`, "placeholder");
- the payload shape and headers are the portal's own invention, **not either provider's callback
  format**, and no code registers a callback with a provider;
- the UI has nowhere to set a webhook secret or see the URL;
- with no secret stored, the key is the **empty string**, so the signature of any body can be
  computed by anyone who knows the connection id *(read, not run)*;
- the timestamp is checked but **not signed**; there is no dedicated rate limit; the body limit is
  the API's general 25 MB.

Both connectors nevertheless declare `SupportsInboundWebhooks = true`.

## 9. Current credential storage

- Credentials go to `ISecretStore`; the connection row holds only an opaque reference.
  Production uses `EncryptedDbSecretStore`: AES-256-GCM per blob (`SecretCipher`), key from
  `Secrets:EncryptionKey`. Both the API and the worker **refuse to start in production** with a
  non-encrypted store.
- Never returned: the API has no credential field in any response; a list returns only the
  **names** of the stored fields. The edit form is blank, shows "Stored" per field, sends only what
  was typed, and the service **merges** typed fields over the stored secret and rotates it.
- Audit entries carry no credential; a rotation is recorded as a boolean.
- **Gaps:** a new credential is saved **before** it is tested (a wrong one replaces a working one);
  credential keys are free-form and unchecked at save; there is no master-key rotation mechanism;
  no webhook secret can be entered.

## 10. Background jobs, retries, rate limits

| Piece | State |
|---|---|
| Job queue (`background_jobs`, `JobProcessor`, 10 s poll) | Works and is tested: retry with exponential backoff, dead letter at five attempts, reprocess from the Jobs page. **One handler exists, the webhook placeholder**; production has never queued a job |
| `ResilientExecutor`, `RetryPolicy`, `CircuitBreaker` | Written and tested (retry transient, honour `RetryAfter`, backoff, breaker). **No production caller** |
| `RateLimitPerMinute`, `MaxRetries`, `RetryBaseDelaySeconds` on every connection | **Read nowhere** |
| Provider rate limits | A 429 is classified; nothing waits, throttles or retries. A 429 on the ticket page fails the run until the next poll; a 429 on a ticket's notes is swallowed |
| Call volume | N+1 per ticket per run: notes, time entries, assignee (ConnectWise also devices). API-triggered calls and the worker share no limiter |
| Inbound API rate limiting | Exists (per user, per organization, alert intake, public forms). Not on the webhook route |

## 11. Logging and audit

- Structured JSON (Serilog) in both processes. The API tags every request with a correlation id
  (`X-Correlation-ID`); **the worker's sync has none**, so a provider call cannot be tied to the run
  that made it.
- No credential is logged. Connector logs are configuration only (field names, picklist labels).
- A provider's **response text** (up to 400 characters, or its `errors[]` joined) is put in the
  exception message, stored in `PsaConnection.LastError` and returned to administrators of that
  tenant.
- Audited today: `connection.created / updated / enabled / disabled / tested /
  settings.updated / fields.refreshed / logo.*`, `mapping.upserted / deleted / rolledback /
  snapshot`, `ticket.resynced / resync_failed`, `attachment.pushed / push_failed`, time-entry
  actions, PSA identity changes. **Not audited:** a manual sync, a webhook, a retry of a failed
  record (there are none to retry).

## 12. Pagination

Only `GetTicketsAsync` pages (and all three implementations were fixed in September after silently
stopping at the first page). **Every other list is one page with no sign that it was cut**:
companies, contacts, technicians, roles, notes per ticket, attachments per ticket, time entries per
ticket, boards, statuses per board, the Autotask attachment sweep. The runner adds its own cap of
50 pages (5,000 tickets) per run and logs a warning when it is reached.

## 13. Error handling

- HTTP status becomes a `ConnectorException` kind consistently. Every transport failure (DNS, TLS,
  refused, cancelled without the caller cancelling) becomes `Timeout`, which is "transient".
- Autotask answers **validation failures with HTTP 500**, which becomes `ProviderError`, also
  "transient". A retry rule that says "retry what is transient" would re-send rejected writes.
- ConnectWise lets a malformed 2xx body escape as a raw `JsonException`.
- Many reads swallow failure and return a neutral value (names, picklists, company names): correct
  for niceties, but with the picklists unavailable Autotask passes **raw ids** through as status,
  priority, queue and category.

## 14. Tenant isolation

- Every table here is a `TenantEntity` behind the global query filter; the resolver looks the
  connection up under the caller's tenant, so another tenant's connection is "not found".
- The worker and the webhook route run under **platform scope** by necessity and then act for one
  connection; everything they write takes the connection's organization id from the connection row,
  never from a payload.
- Client roles hold no connection, mapping, health, jobs or audit permission (confirmed in
  `Permissions.ForRole`). A client user with the control-panel grant can trigger a live read of
  their **own** company's contacts, devices, agreements and holidays: an existing, designed
  feature, scoped by their company.
- **Where platform scope is not followed by a tenant filter, isolation is lost.** Two places:
  the sync runner's mapping-rule load (T1) and the activity rollup's technician maps (T3). Both are
  harmless with one organization and wrong with two.
- **Where a technician is identified without the connection, isolation between accounts is lost**
  (T2, T3).
- **No test uses two connections of the same provider in one tenant, and none runs a sync for one
  tenant while another tenant holds rules.**

## 15. Provider-specific logic outside the connector layer

70 places, counted by a read-only review and spot-checked by hand. Most are harmless; nine decide
behaviour.

| Kind | Count | Examples |
|---|---|---|
| A label (provider number to name) | 26 | `AttentionService`, `WorkPlanService`, `badges.tsx`, the health page, `SyncSettings.tsx` |
| Registration (factories, named HTTP clients, the enum) | 6 | `DependencyInjection`, `ConnectorResolver` |
| Filtering by "the provider of this ticket" | 20 | The mapping-rule loads and the mapping admin API (§7) |
| **Behaviour that differs by named provider** | 9 | `PsaTicketLink` (which deep link); `ConnectionAdminService` (ConnectWise tenant identifier default); `AuthorBackfill` (skips ConnectWise); `SyncSettings.tsx` ×4 (Autotask role rules); `tickets/[id]/page.tsx` (notes required when logging time); `WorkforceSchedule.tsx` (a work-source filter listing Autotask and ConnectWise by name) |
| **An assumption about one provider's data** | 9 | Deep-link URL shapes; `DeviceSyncService` needs a **numeric** company id; `PsaConnection` carries Autotask-named default columns; the factory strips `/v1.0`; a byline `"{Provider} automation"` is stored and later matched by its ending; the connections page hard-codes each provider's credential field names; **the schedule derives a block's source by testing whether its reference starts with "Autotask " or "ConnectWise "** |

The core services read the normalized ticket and hold no provider branch, with one exception that
breaks the brief's rule: **the scheduling screen** names the two providers and recovers the source
from a display string. A third provider would show there as "Team board". Otherwise **the leak is
in the admin screens**, which should ask the server what a connection can do.

One trap in the shared model: `UnifiedTicket.RequesterExternalId` carries the **company** id on the
way in and the **contact** id on the way out.

## 16. Current tests

More than 200 tests touch this layer. Worth naming:

- **A connector certification suite already exists** (`tests/unit/Certification`): 21 contract
  tests inherited by the mock, Autotask and ConnectWise (63 runs), against **HTTP-level fakes**
  (`FakeAutotaskServer`, `FakeConnectWiseServer`), plus 34 provider-specific and 3 cross-provider
  normalization tests.
- Sync: upsert, echo, unmapped reporting, notes (25), attachments (18), unsynced tickets and resync.
- Mapping engine (10), connection and mapping admin with versions and rollback (22), secrets (13),
  jobs (3), resilience (8), PSA identity (5).

**No test at all for:** provider rate limiting; a sync run meeting a 429 or a timeout; paging in
the runner (its stub returns one page); the 50-page cap; the cursor after a failure; two connections
of one provider; saving an unsafe endpoint; the egress guard's handler; the webhook controller; an
empty webhook secret; a duplicate time entry after a lost reply. No browser test opens the
connections, mappings, health or jobs pages.

---

## 17. Findings

### Security

| # | Severity | Finding | Evidence |
|---|---|---|---|
| S1 | **High** | **A connection can be pointed at any address, and the worker will call it.** The endpoint is saved unchecked (any scheme, any host). The guard that refuses private and reserved addresses is optional, off by default, and in the production compose file is switched on for the API **but not for the worker**, which runs every scheduled sync. The stored credentials are sent to whatever the endpoint is. The guard also resolves the name itself while the real request resolves it again, and redirects are followed below it | `ConnectionAdminService` (create, update); `DependencyInjection` (`Connectors:BlockPrivateEgress`); `docker-compose.prod.yml` (api has it, worker does not); confirmed on the running containers |
| S2 | **High when webhooks are used; low today** | With no webhook secret stored the signature key is empty, so anyone who knows a connection id can have an event accepted. The timestamp is not signed. Today the only effect is a row and a job that logs *(the empty key is reproduced: the certification test `With_no_webhook_secret_stored_no_delivery_is_valid` fails for all three connectors on the code as audited)* | Factories default the secret to `""`; both connectors' `ValidateWebhookAsync` |
| S3 | Medium | A manual sync runs inside the request with no lock and no limit on repeats: it can be started many times over, and alongside the worker's own run | `AdminController` sync route; no lock anywhere |
| S4 | Medium | A new credential replaces the stored one before it is tested | `ConnectionAdminService.UpdateAsync` |
| S5 | Low | A connection can be created for a provider that has no connector; a new connection is **enabled at once**, before any test, mapping or preview | `CreateAsync` (`IsEnabled = true`) |
| S6 | Low | Capability flags overstate: webhooks (both), custom fields (ConnectWise) | `GetCapabilitiesAsync` in both |

Exposure of S1 today is limited by there being one organization, whose two endpoints are public. It
becomes material the day a second tenant can add a connection.

### Isolation between tenants and between accounts

| # | Severity | Finding | Evidence |
|---|---|---|---|
| T1 | **High** (latent: one organization today) | **The scheduled sync loads every tenant's mapping rules for the provider.** The worker runs under platform scope, which switches the tenant filter off, and the rule load filters by provider only. Rules at connection scope are still held to their connection, so today's 52 rules are safe. A rule at provider, platform, custom-field or conditional scope, which the API accepts although the screen never sends one, would apply to **every organization's** tickets of that provider: one tenant could change how another's statuses are mapped. A rule's connection id is also stored as sent, so a rule can be filed against another organization's connection *(reproduced: `MappingIsolationTests` fails on the code as audited)* | `ConnectionSyncRunner` (rule load) under `PollingSyncService` (`SetPlatformScope`); `DeskDbContext` (`BypassTenantFilter`); `MappingEngine.ScopeApplies` |
| T2 | **High. Live today** | **"Assigned to me" does not follow the PSA link.** Ticket visibility for a technician, and for a department or team, finds the person's PSA id in `app_users.ExternalTechnicianId`. Nothing writes that column: it is empty for all 41 people. The links people actually have are in `user_psa_identities`, which this code does not read. So a linked technician limited to their own tickets (all 3 linked people, and 40 of 41 staff, have that limit) sees a PSA ticket only once it has a portal holder, and none of the 40 PSA-assigned tickets has one. The tests pass because they set the empty column directly. **The same column is the multi-account defect**: it holds one id for all connections, so if it were filled, a technician whose id on one account equals someone else's on another would be shown that person's tickets *(reproduced: `TicketScopeQueryTests`, seeded the way production stores a link, fails on the code as audited)* | `TicketScopeQuery` (`AssignedOnlyAsync`, `GroupOrUnassignedAsync`); `DeskClaimsTransformation` (the `desk_tech` claim, which feeds "my" dashboard figures); `TicketScopeQueryTests` (seeds the column) |
| T3 | Medium | **People are merged by a bare PSA id.** The key for a person without a portal account is `x:{id}` with no connection; team figures, the ticket list's person filter, coverage, satisfaction and the staff report group on it. The activity rollup builds its id-to-person maps **across all tenants** and stores the result in facts that have no connection column | `PersonKey`; `TechnicianMetricsService`; `PortalCoverageService`; `ActivityRollupService` (maps built with the tenant filter off) |
| T4 | Medium | **A client's first sign-in fails if their e-mail is invited twice.** The one-time bind looks the e-mail up across all tenants and expects at most one row; e-mail is unique only within a company, and a company belongs to one connection. The same contact under two connections, or two tenants, is two rows and an error. The staff bind has the same shape across organizations *(reproduced: `FirstSignInBindTests`; no pending invitation exists today)* | `DeskClaimsTransformation.TransformClientAsync` |
| T5 | Low | The same PSA account can be connected twice, and two connections can share a name. Saved ticket views and the list's connection and queue filters match on **names** | no unique index on `psa_connections`; `TicketReadService`; `SavedTicketView` |
| T6 | Low | References shown to people omit the connection ("Autotask 12345"): two accounts can both have ticket 12345 | `WorkPlanService.Reference`; notifications; attention items |

### Data loss and sync correctness

| # | Severity | Finding | Evidence |
|---|---|---|---|
| D1 | **High** | **A change made during a sync run can be missed for good.** The cursor is set to the time the run **ended**, with no overlap. A ticket read on page 1 and changed again while page 3 is being read has a modified time before the new cursor, so the next run does not ask for it *(reproduced)* | `ConnectionSyncRunner` (cursor stamped after the loop) |
| D2 | **High** | **A large import never completes.** At 50 pages the run stops, logs a warning, and still advances the cursor. The remaining tickets are never requested again; a full re-sync reads the same first 5,000 *(reproduced with 5,001 tickets: 5,000 imported, the next run fetched none, the connection showed Healthy)* | same |
| D3 | **High** | **A full re-sync can delete stored attachments.** On a full sync the runner treats the provider's attachment list as complete and deletes every imported file (row and bytes) missing from it. Autotask's list is **one page of 500**. A connection with more than 500 ticket attachments would lose the rest when someone presses "Re-sync all" *(read, not run; production holds 25)* | `GetRecentAttachmentsAsync` (one page) feeding `ReconcileDeletionsAsync` |
| D4 | Medium | Every non-ticket list stops at one page without saying so: the 501st company, technician or note is invisible | both connectors |
| D5 | Medium | A rate limit while reading a ticket's notes or time is swallowed; the run ends Healthy and the cursor moves on, so those notes are not read again until the ticket changes *(reproduced)* | runner `catch (ConnectorException)` ×6 |
| D6 | Medium | One ticket that cannot be saved fails the whole run, every run: the connection stays Degraded behind it *(reproduced)* | no per-record isolation |
| D7 | Medium | The worker and a manual sync can run together on one connection. Tickets are protected by their unique index (one run fails); notes, attachments and time entries have none and can be doubled | no lock; no unique index on the three tables |
| D8 | Medium | **A retried write can duplicate in the PSA.** Neither connector uses the idempotency key. A time entry whose reply was lost is marked Failed and "Retry" posts it again | `TicketTimeWriter.PushAsync`; connectors' `AddTimeEntryAsync` |
| D9 | Low | A queue or board filter cannot work: the ids are sent to the provider, and the returned tickets are then compared against the queue's **name**, so every ticket is rejected *(reproduced: `ImportFilterTests`; no filter is set in production)* | runner `Passes` against `QueueOrBoard` |
| D10 | Low | Two people can be linked to one PSA login on a connection; the later link wins silently, and listing a connection's technicians would then fail | index on `user_psa_identities`; `TechnicianProvisioningService` |
| D11 | Low | When a run fails on a database error, recording the failure uses the same unit of work and can fail the same way, leaving the connection showing its last good state *(read, not run)* | runner `catch` block |

### Reliability and design debt

| # | Finding |
|---|---|
| R1 | No throttling, no retry, no timeout on provider calls; the settings and the executor for them exist unused |
| R2 | Connections are polled one after another across all tenants: one slow or rate-limited connection delays every other |
| R3 | Outbound writes hold the request for as long as the PSA takes (up to 100 s) |
| R4 | Unknown provider values pass through as if mapped; nothing persistent says "unmapped" |
| R5 | The same real client in two PSAs is two clients; clients are created without a decision |
| R6 | Work type has one level; subtype, item, issue and sub-issue are not read |
| R7 | The admin screens branch on provider numbers and hard-code provider lists; capabilities never reach the browser |
| R8 | The integration health snapshot repeats tenant-wide job counts on every connection and features the first connection beside totals of all |
| R9 | `sync_events` rows are never marked processed and never pruned |
| R10 | Connector code is duplicated where a shared HTTP layer would serve both and every later provider |
| R11 | A background job is claimed by a plain read and marked running only after its handler returns: two workers would run the same job. One worker runs today |

### Breaking-change risks in doing this work

| Risk | Control |
|---|---|
| Changing the cursor re-imports history | The new cursor is seeded from each connection's current `LastSuccessfulSyncAt`; a re-read ticket is a no-op by hash. Reconciled before and after (§19) |
| A unique index fails on existing rows | Duplicates counted in production first (none today); the migration is rehearsed on a copy |
| The endpoint rule rejects a live connection | Both production endpoints pass it (public, https); checked in a test with their real host names |
| The guard blocks a self-hosted PSA on a private network | An explicit operator allow-list (`Connectors:AllowedHosts`), not a tenant setting |
| Retrying makes duplicates | Only reads are retried automatically; a write is retried only when the provider said it did not process it (429) |
| Shared HTTP layer changes connector behaviour | The certification suite (63 runs) must pass unchanged before and after |
| **Fixing T2 changes what three people see** | It widens their list to the PSA tickets assigned to their own linked login, which is what the code's own comment says already happens. Stated in the pull request; the before and after lists are counted for each of the three |
| Loading rules by connection drops a rule someone relied on | Production has no rule outside connection scope (all 52 checked) |

---

## 18. Classification of every proposed change

| Area (brief §) | Decision | What |
|---|---|---|
| Provider adapter contract (4) | **REUSE** | `IServiceManagementConnector` is that contract. Not renamed |
| Canonical model (3) | **REUSE** | `Ticket`, `ClientCompany`, `ClientUser`, `TicketNote`, `TicketTimeEntry`, `TicketAttachment`, `Device`. No parallel "work item" model |
| Capability model (5) | **EXTEND** | Make the flags truthful, add the ones the UI needs, return them from the API, drive the screens from them |
| Multiple connections per tenant (6, 10) | **REUSE + REFACTOR** | Already modelled and collision-safe for tickets and companies. **Not** yet safe for technicians and mapping rules: see the next three rows. Add the missing unique indexes and tests with two connections of one provider |
| Technician identity (18, 99) | **REFACTOR** | Every reader resolves a technician as connection + id through `user_psa_identities`: ticket visibility, the claim, person keys, metrics, coverage, the rollup. The per-person column is retired |
| Mapping rule isolation (12, 99) | **REFACTOR** | Rules are loaded by organization **and** connection everywhere; scopes without a connection are refused at save and ignored at read |
| Tenant filter under platform scope (99) | **REFACTOR** | Every read the worker makes for one connection names that connection's organization explicitly |
| Client sign-in with one e-mail in two companies (99) | **REFACTOR (minimal now); design in the mapping slice** | An ambiguous bind fails closed with a clear message instead of an error. One login reaching two companies is a product decision, not taken here |
| Duplicate connection, names as keys (6, 10) | **EXTEND** | Refuse a second connection to the same account; filters and saved views key on the connection id |
| References that name the connection (11) | **EXTEND** | The server returns source and connection as fields; the schedule stops parsing a display string |
| External references (11) | **REUSE; NEW only for clients** | Per-entity (connection, external id) columns stay. A reference table is added **only** where one PIO record must answer to several external ones: a client mapped from more than one connection |
| Endpoint safety, SSRF (100) | **NEW + EXTEND** | Validate at save; guard on by default in both processes; pin the checked address; no redirects |
| Shared provider HTTP layer (49–52, 68–69) | **NEW (REFACTOR of duplicates)** | One handler for timeout, per-connection throttle, bounded retry with `Retry-After`, structured log with correlation id. Wire the unused settings and executor |
| Sync cursors (38–40) | **NEW** | Per connection and entity; watermark from the run's start with an overlap; advanced only after durable processing |
| No silent cap (37) | **REFACTOR** | A run that has more to read says so and continues from where it stopped |
| Full paging of every list (37) | **REFACTOR** | Both connectors; a list that was cut is never treated as complete |
| Failed sync records (53) | **NEW** | A record that could not be applied is kept with its category and attempts, retried with backoff, then held for a person |
| Sync runs, lock, health (54, 71, 72) | **NEW** | A run record per connection doubles as the lease; "Sync now" joins or reports "in progress" |
| Idempotent upsert (41–43) | **REUSE + EXTEND** | Tickets and companies are. Unique indexes for notes, attachments, time entries |
| Time-entry duplicate on retry (44) | **NEW** | Look for the entry in the PSA before posting again |
| Retry classification (51) | **EXTEND** | Kinds exist. Add "safe to repeat" per request, so a rejected write is not re-sent |
| Connection states, pause, disconnect (55–58) | **EXTEND** | Setup, Paused and Auth required join the existing states; a new connection starts in Setup. Delete stays **archive**: nothing imported is removed |
| Credential rotation (61), secret display (60) | **EXTEND** | Test before save. Display is already write-only |
| Connection wizard, catalog, test matrix, preflight (7–9, 88, 89) | **NEW** | Guided set-up over the existing services; 2 providers available, the rest "coming soon" with no connector behind them |
| Initial sync preview and scope (34–36) | **NEW + EXTEND** | Counts before import; scope chosen from discovered lists instead of typed ids |
| Mapping engine (12–14) | **REUSE** | Connection-scoped rules and the engine stay |
| Unknown values (16), mapping health (29), validation (28), preview (27) | **NEW** | A persistent register of unmapped values per connection; blocking / warning / optional; preview on real samples |
| Status, priority, queue mapping (15, 17, 22) | **REUSE + EXTEND** | Exist. Add the unmapped state and connection-scoped API |
| Technician mapping and suggestions (18, 19) | **EXTEND** | Add Ignored; suggest on exact e-mail only, confirmed by a person. Never create a user unasked |
| Client mapping and duplicate detection (20, 21) | **NEW** | Map to existing, create, or ignore; suggest by exact external reference or domain, never by similar name |
| Work type mapping (23) | **EXTEND** | Read subtype / item / issue / sub-issue, keep the raw values, map to a portal work type |
| Custom fields (24–26) | **NEW, read-only, typed** | Controlled transformations only; no scripting |
| Mapping versioning (30) | **REUSE** | Exists and is audited. Add the missing screen for history and rollback |
| Sync direction and field ownership (31–33) | **DONE** | The ownership matrix |
| Outbound queue (73, 74) | **EXTEND for local-first writes; NOT for write-through** | Ticket create, time entry and attachment get a common pending / failed / retry path. Status, assignment and notes stay provider-first: PIO never shows a value the PSA has not accepted |
| Conflict centre (64–66) | **NOT REQUIRED now** | With one writer of record per field there is nothing for it to show (matrix, "Conflicts"). Recorded as required the day an outbound queue lets PIO hold an unconfirmed value |
| Webhooks (45–48) | **EXTEND the frame; DEFER provider adapters** | Make the frame safe (secret required, timestamp signed, size and rate limits, real handler: fetch then idempotent upsert). Provider-native callbacks for Autotask and ConnectWise are their own slice. Polling remains the baseline |
| OAuth (101) | **NOT REQUIRED now** | Neither provider uses it. The guide records what a provider that does will need |
| Raw payload storage (67) | **NOT REQUIRED** | None is stored; structured logs only |
| Internal tickets, search, analytics, scheduling (79–83) | **REUSE** | Already provider-neutral; protected by regression |
| Contract tests, mock provider, fixtures (93–95) | **EXTEND** | The certification suite and HTTP fakes exist. Add faults they cannot make today: `Retry-After`, fail then succeed, failure on a later page, timeouts, duplicates |
| PSA-side time entries as worklogs | **DEFER (decision needed)** | Importing them would change what Phase 7 and 8 count as recorded work. Not done without a decision |
| Future providers (HaloPSA and the rest) | **DEFER to Phase 10** | No fake connector |
| Deleting a connection's imported data | **NOT REQUIRED** | Archive only |

---

## 19. Migration and reconciliation

- **No data is moved.** External ids, tickets, clients, technician links, mappings, time entries
  and credentials stay where they are. New tables are additive.
- **Cursor seeding.** Each connection's new cursor starts at its current `LastSuccessfulSyncAt`.
- **Before and after each slice that touches sync**, the same read-only counts are taken in
  production and compared: tickets per connection, open and closed, companies per connection,
  PSA identities, notes and imported notes, attachments and imported attachments, time entries by
  state, mapping rules by field, distinct statuses and priorities. Any unexplained difference fails
  the slice. The baseline is §2.

## 20. Delivery

This phase changes how live sync behaves against real PSA data, so it is delivered as **slices**,
each its own pull request, mergeable, deployable and reversible alone. The exit gate of the brief is
judged at the end of the last one; each slice reports which gate items it closes.

| Slice | Content | Closes |
|---|---|---|
| **1. Isolation and safety** | Technician identity by connection in ticket visibility, the claim and the rollup; mapping rules loaded by organization and connection, a rule's connection checked at save; the endpoint rule and the guard in both processes; the webhook route refuses a connection with no secret and signs the timestamp; a truncated list never deletes anything; the queue filter; a connection needs a real connector; a first sign-in binds only when unambiguous. No new table | T1, T2, T3 (rollup), T4, S1, S2, S5 (part), D3, D9 |
| **2a. Sync engine** | Sync cursors, runs and the per-connection lock; a read that runs out of pages is continued; per-record isolation; failed sync records with retry, review and dismissal; routes to read a connection's sync state; one person per PSA login. One migration, additive | S3 (the lock), D1, D2, D5, D6, D7, D10, D11 |
| **2b. Provider calls** | The shared provider HTTP layer (a bound on each attempt, a per-connection budget, retry of what is safe to repeat, `Retry-After` in both forms); every list read to its end; a retried time entry checked against the PSA first | D4, D8, R1, R10 (the transport; the two connectors still map errors separately) |
| **3. Connections** | States, pause, archive; test before saving a credential; no duplicate account; capabilities from the API; provider catalog; the add-connection wizard with the test matrix, discovery, scope from lists, preview and preflight; sync health and freshness on the connection; filters, views and person keys keyed by connection id | S4–S6, T3 (person keys), T5, T6, R2, R7, R8 |
| **4. Mapping** | The unmapped register, mapping health, validation and preview; connection-scoped mapping API and screens with history; technician states and suggestions; client mapping (and the decision on one login for two companies); work type; custom fields | R4–R6 |
| **5. Outbound reliability** | One pending / failed / retry path for local-first writes; reconciliation before any retried create; outbound state on the ticket | D8 (rest), R3 |
| **6. Webhooks** | Provider-native callbacks for the two connectors on the safe frame; hybrid with polling | brief §45–48 |
| **7. Certification** | The contract suite as the certification a new connector must pass; `ADDING_NEW_PSA_CONNECTOR.md`; required permissions per provider; performance at volume | brief §93–95, 104, 108, 109 |

Slice 1 is deliberately small and has no migration: it closes the two isolation defects, one of
which affects people today, and the security finding, and can be deployed on its own.

**Nothing found makes it unsafe to begin.** Slice 1 starts now, on the branch
`feat/psa-phase9-connector-framework`.

## 20a. Decisions taken by the owner (6 October 2026)

Five questions this work could not answer for itself. Each was put to the owner and answered, with
what each answer requires.

| # | Question | Decision | Required of the build |
|---|---|---|---|
| 1 | May one client login reach more than one company? | **Yes, only through explicit, authorized mappings of that client user to each company** | Authorization on the server on every request. Access is never inferred from an e-mail domain. Least privilege by default. No ticket of a company the user is not mapped to, ever. View and Create granted per company where the present permissions allow. Tests for a guessed id (IDOR / BOLA) and for crossing companies. Every change to a mapping audited |
| 2 | Are outbound changes queued when the PSA is down? | **Yes, for the changes that support it.** Three states: *Pending Sync*, *Synced*, *Sync Failed* | The local side of an operation is saved only as the ownership matrix allows: a field the PSA owns is not overwritten locally because the PSA could not be reached. The operation is queued, retried with exponential backoff and jitter inside the provider's rate limits, and kept after its retries run out. An authorized person may retry it. Nothing is called *Synced* before the provider has confirmed it |
| 3 | Does time entered directly in the PSA become worklogs here? | **Yes** | The source and provider, the external time-entry id and the connection are kept. The import is idempotent: one worklog however often polling, a webhook or a retry delivers the entry. The technician mapping, and the original time and duration, are kept. An imported worklog is plainly marked as such, and is never sent back to the PSA as a new entry |
| 4 | Is a PSA's classification translated into the portal's own? | **Yes, only through configurable mapping.** ConnectWise type / subtype / item and Autotask ticket type / issue type / sub-issue type are kept as they are, and may be mapped to a portal **Category**, **Work Type** and **Subcategory** | Mappings belong to a connection. An unknown value is never guessed: it is *Unmapped*. A preview and mapping health. Changes audited. A technician's skills are not derived from it unless that is configured |
| 5 | Which custom fields are shown? | **None is hard-coded.** An administrator selects a provider's custom fields and sets for each: import or ignore, the portal field it goes to, *Internal only* or *Client visible*, and read-only or editable (editable only where the provider and the security model allow) | *Internal only* is the default. No custom field reaches a client user unless it was explicitly approved for them |

Three further requirements came with the answers:

- **Performance before Phase 9 closes.** The three reads [performance.md](performance.md) left
  at 3 to 6 seconds on 500,000 tickets (the dashboard summary, the ticket page's filter lists, text
  search) are to be profiled and made faster, with before and after, the data set, the query
  counts, the indexes added and what each costs written down. Nothing is to be hidden behind a
  cache that can be stale or unsafe.
- **Certification against the real PSAs.** Phase 9 is not production-certified until it has been
  run against a real Datto Autotask and a real ConnectWise PSA test environment. A check that
  cannot be run is recorded as **BLOCKED - TEST ENVIRONMENT REQUIRED**, never as a pass on the
  strength of a stand-in.
- **No merge and no deploy** without the owner's word, and Phase 10 is not begun. The delivery
  tracker ([PHASE9_DELIVERY_TRACKER.md](PHASE9_DELIVERY_TRACKER.md)) gives each slice's branch,
  files, migration, tests, security and performance results, limits, commit and pull-request state.

## 21. Progress

| Slice | State | Evidence |
|---|---|---|
| 1. Isolation and safety | **Built**, awaiting review and deploy | 116 new tests; 1,389 pass in both time-zone modes, and the 16 PostgreSQL 17 tests pass. T1, T2, T3, T4, S2 and D9 each have tests that were run against the code as audited and fail there; S1, S5 and D3 are covered by tests of the new behaviour. The ticket-visibility predicate, built by hand as one clause per PSA connection, is also run through a SQL translator (`RelationalQueryTests`) and on PostgreSQL 17 (`CapacityPerformanceTests`); before this it had only ever run in memory |
| 2a. Sync engine | **Built**, stacked on slice 1 (its pull request opens when slice 1 is merged) | 32 more tests; 1,421 pass in both time-zone modes. D1, D2, D5 and D6 were reproduced against the code as audited by a throwaway probe (four tests asserting the defect, all passing there). The run, its lock and its failure store also run through a SQL translator, including a save the database genuinely refuses; on PostgreSQL 17 eight runs started at the same instant end with one, and the migration applies. See [sync-engine.md](sync-engine.md) |
| 2b. Provider calls | **Built**, stacked on 2a | 35 more tests; 1,456 pass in both time-zone modes. See [provider-calls.md](provider-calls.md). D8 and D4 were confirmed by reading and are covered by tests of the new behaviour; the two fake PSA servers were corrected where they hid the defects (the ConnectWise fake returned every note whatever page was asked for and had no time entries at all; the Autotask fake kept a created time entry without the id it answered with) |
| 3a. Connection lifecycle | **Built**, stacked on 2b | 37 more tests; 1,493 pass in both time-zone modes, and 2 more browser tests (72 pass in Chromium). The new connection queries also run through a SQL translator in every state, and the migration was applied to a PostgreSQL 17 database from the generated script. See [connections.md](connections.md). S4 and S5 are closed, and the first half of T5 (the same PSA account connected twice). Driven in a browser against a stand-in PSA: a connection whose keys are rejected stays in setup with the reason, corrected keys switch it on, and pause, resume, disable, enable, archive and restore each do what the screen says. One migration, additive (five columns on `psa_connections`) |
| 3b. Add-connection wizard | **Built**, stacked on 3a | 13 more tests; 1,506 pass in both time-zone modes, and 73 browser tests pass in Chromium. Three of them drive the wizard: one checks the catalog and each PSA's own fields, one runs against a closed port (the connection stays in setup and the API refuses to switch it on), one from the first step to the last against a stand-in ConnectWise started inside the test, with the real connector making real HTTP calls. In that run the count carries the board that was ticked and not one request other than a GET reaches the PSA. S6 is closed. No migration |
| 3c. What belongs to a connection | **Built**, stacked on 3b | 10 more tests; 1,516 pass in both time-zone modes, and the 73 browser tests pass in Chromium. T3, the rest of T5 and T6 are closed. For T3 each place a person is counted (team table, daily hours, satisfaction, ticket list and its filter, client workload, portal coverage) has a test asserted from what the service returns, and five of the six that the old code can compile fail against it. The pinned query budgets are unchanged. No migration |
| 4a. Mapping health | **Built**, stacked on 3c | 8 more tests; 1,524 pass in both time-zone modes, and the 73 browser tests pass in Chromium (the wizard's test now also reads the report and the sample). See [mapping-health.md](mapping-health.md). R4 is closed: what a PSA sends that nothing maps is reported with the tickets that hold it and what the portal is doing with them meanwhile, and a new rule can be applied to tickets already imported. Nothing is stored for it; no migration |
| 4b. Mapping what a PSA sends | **Built**, stacked on 4a | 7 more tests; 1,531 pass in both time-zone modes, and the 73 browser tests pass in Chromium (the wizard's test now maps two statuses on the Field Mapping page, from a suggestion, and checks nothing is saved until the list of changes is). The Field Mapping page could save one rule per portal status, and the API overwrote a second PSA status mapped to the same one: production's inbound rules for its other PSA statuses were not made on that page, which could not make them. Any number of PSA values can now be mapped to one portal value through the product, staged and reviewed before saving, saved as one version and audited with what each value was mapped to before. No migration |
| 4c. Technicians: linked, not linked, left alone | **Built**, stacked on 4b | 3 more tests; 1,534 pass in both time-zone modes, and the 73 browser tests pass in Chromium (the wizard's test now leaves the PSA's API account alone from the Mapping panel). The list of a PSA's technicians, the suggestion by exact e-mail and the one-at-a-time "Add" were already there. What was missing was a way to say a login is nobody (an API account, someone who left), so the list never emptied. One migration, additive (`psa_technician_ignores`), applied to PostgreSQL 17 from the generated script |
| 7 (first part). Certification and the new-connector guide | **Built**, stacked on 4c | 11 more tests; 1,545 pass in both time-zone modes. [ADDING_NEW_PSA_CONNECTOR.md](ADDING_NEW_PSA_CONNECTOR.md), the fourth document the brief requires, is written from the code as it is, including the places outside a connector that still branch on the provider. The certification suite gains what Phase 9 added to the contract (a count that agrees with the read; safety of what is not claimed) and runs against all three connectors, 26 contract tests each; a second certification holds every factory's descriptor and account key. Provider names and tile initials are in one place and reach the browser with the catalog. No migration |
| 7 (second part). Performance at volume | **Built**, stacked on the first part | 7 more tests; 1,552 pass in both time-zone modes, and the volume tests and the benchmark pass on PostgreSQL 17. Measured at 10 connections and 100,000 tickets, and at 100 connections, 500,000 tickets and 1,000,000 time entries: [performance.md](performance.md). The measuring found four things and all four are fixed: a sync held every ticket it had read, so each cost more than the last (the same 5,000-ticket test: 9 min 1 s to 3 min 1 s on PostgreSQL); applying a mapping went client by client and board by board (965 queries to 49); the Connections page counted the tickets once for every connection (14.4 s to about 0.12 s at a hundred connections and half a million tickets); and a page of the ticket list read every ticket three times. Three older reads of the ticket list are measured and not changed: the dashboard summary, the filter lists and text search take 3 to 6 seconds at half a million tickets. No migration |
| 6 (first part). "Sync now" asked for and run by the worker; the job queue's claim (R11) | **Built**, stacked on the volume slice | 22 more tests; 1,574 pass in both time-zone modes, the 73 browser tests pass in Chromium (the wizard's test now follows a sync from asked for, to running, to what it did), and on PostgreSQL 17 six workers taking the same thirty jobs take each once, with 150 saves refused. "Sync now" ran the sync inside the web request; a large first import is longer than a request is allowed to be. It is now a note on the connection that the worker reads within five seconds; the request answers at once and the card follows the sync. A background job is taken by a save of its own, checked by a version, and held for a lease, so two workers cannot run one job and a worker that stops does not strand it. One migration, additive: three columns on `psa_connections` and two on `background_jobs`, applied to PostgreSQL 17 from the generated script |
| 4d. What a PSA files a ticket under (R6, first part) | **Built**, stacked on 6a | 8 more tests; 1,582 pass in both time-zone modes, the 73 browser tests pass in Chromium, and the web's own 12 unit tests pass (4 of them new). The portal read a ticket's category and nothing under it. It now reads Autotask's ticket type, issue type and sub-issue type and ConnectWise's type, subtype and item, keeps them as the PSA sent them, and shows them to staff on the ticket under the PSA's own names for the levels. A client is not shown them. A ticket already here gets them the next time it is read; one filed under nothing is not rewritten. Every connector must now read back what a ticket is raised under (27 contract tests each). Nothing is translated: whether the levels should also become work categories of the desk's own is the owner's question 4. One migration, additive: three nullable columns on `tickets`, applied to PostgreSQL 17 from the generated script |
| 7c. The three slow reads of the ticket list | **Built**, stacked on 4d | 15 more tests; 1,597 pass in both time-zone modes, the 73 browser tests pass in Chromium, and the volume tests and the benchmark pass on PostgreSQL 17. Profiled first: each was a pass over a 434 MB table for every figure it shows. At 500,000 tickets the search went from 2.4 to 4.0 s to 28 to 140 ms, the dashboard summary from 2.4 to 6.4 s to 0.35 to 0.62 s, and the filter lists from 2.6 to 4.9 s to 0.38 to 0.59 s. Nothing is cached and each answers what it answered before, which is now held to the figure by tests that did not exist. Five indexes, 93 MB at that size; what each costs is in [performance.md](performance.md). One migration: a nullable column on time entries with a backfill, five indexes and the `pg_trgm` extension, applied to PostgreSQL 17 from the generated script |
| 4e. What a PSA's classification means in the portal (R6, the rest) | **Built**, stacked on 7c | 15 more tests and one browser test; 1,612 pass in both time-zone modes, the 74 browser tests pass in Chromium and the web's own 16 unit tests pass. Rules for one connection say what tickets filed under some of the PSA's three levels are here: a category, a work type, a subcategory. Each is taken from the most exact rule that gives it. A classification no rule names is UNMAPPED and is given nothing; there is no rule for everything else. The PSA's own levels stay on the ticket. A preview says what rules would do before they are saved; saving touches no ticket; applying gives the tickets already here what the sync would, by id and without loading them. Counted in the connection's mapping health once it has a rule. Every change audited; another organization's connection is not found; the work type and subcategory reach staff only. See [classification-mapping.md](classification-mapping.md). One migration, additive: a table and a nullable column, applied to PostgreSQL 17 from the generated script, twice |
| Rest of 4, 5, 6 | Not started, and no longer waiting: the owner answered on 6 October (section 20a). To build: custom fields chosen by an administrator; time entered in the PSA as worklogs; a client user's companies; outbound changes queued when the PSA is down (5). Left in 6: provider-native webhooks, which need the owner's Autotask sandbox and ConnectWise test environment to be proved | |

What slice 1 changes for people, stated here because two of them are visible:

- A technician linked to a PSA login now sees the PSA tickets assigned to that login, and their own
  rows in workforce analytics name those tickets instead of "Work you cannot open". In production
  that is three people.
- A connection's API address is checked when it is saved. Both production connections pass.
- The worker now refuses private addresses, as the API already did.
- A connection limited to certain queues or boards now imports them. No production connection has
  such a limit.

What slice 2a changes for people:

- "Sync now" while a sync is already running says so and starts nothing. It used to start a second
  run over the same tickets.
- A large import finishes over several runs instead of stopping at 5,000 tickets, and says "more to
  read" while it does.
- One ticket the portal cannot save no longer blocks the connection; it is listed as a failed record
  and tried again.
- A PSA login can be linked to one person on a connection. Saving a second link says who holds it.

What slice 2b changes for people:

- A sync of a large PSA paces itself (two requests a second once a burst is spent) instead of being
  told to stop by the PSA. An ordinary sync is no slower.
- A PSA that does not answer is given up on after a minute, and a read is tried again by itself.
- Retrying a time entry whose first push went unanswered no longer risks a second entry in the PSA:
  the PSA is asked first.
- Lists longer than one page (more than 500 companies or technicians in Autotask, more than 1,000
  in ConnectWise) are read in full.

What slice 3a changes for people:

- A new connection is saved switched off, tested at once, and switched on only if the PSA accepts
  it. One the PSA rejects stays in setup with the PSA's answer on its card, and syncs nothing.
- New credentials or a new address on a working connection are tried before they are kept. A
  mistyped key no longer replaces a working one: it is refused, and the connection carries on.
- The same PSA account cannot be connected twice.
- A connection can be paused (reading stops, replies and time still go out), disabled, and archived
  (put away with everything it imported, and restorable).
- When a PSA rejects a connection's credentials, the portal stops asking every five minutes and
  says the connection needs new credentials, instead of risking the API account being locked.
- The connection's card shows one state, its recent sync runs and who started them, and any record
  the sync could not read, with a way to try it again or stop trying.
- The needs-attention list no longer calls a half-set-up connection "failed", reports a paused sync
  as paused, and is silent about archived connections.
- Editing a disabled connection no longer switches it on.
- The PSAs that can be connected, and the fields each asks for, come from the connectors. Planned
  PSAs are shown as coming soon and cannot be chosen.

What slice 3b changes for people:

- **Add connection** opens a wizard of nine steps: choose the PSA, details, credentials, test,
  what the PSA offers, mapping, sync scope, preview, enable. The connection is switched on only at
  the last step, and a wizard closed part-way is carried on from the connection's card.
- The test is a list, not a word: authentication, each read the sync needs, and what else the PSA
  allows. It never writes to the PSA; a write is shown as "not tried".
- A PSA account that can sign in but cannot read tickets no longer passes the test.
- Before anything is imported the wizard says how much there is: clients, technicians, open and
  total tickets under the chosen scope, and how many values have no mapping.
- Queues or boards to import are ticked from the PSA's own list.
- A connection whose scope would import nothing, or that is missing a credential, cannot be
  switched on, and is told why in words.
- The portal no longer says a PSA supports webhooks, or ConnectWise custom fields, when the
  connector does not use them yet.

What slice 3c changes for people (nothing today; it matters from the second account of one PSA):

- Two technicians who have the same id in two PSA accounts are two people in every table, total and
  filter. They were one.
- Two connections cannot be given the same name.
- With two accounts of the same PSA, a ticket is referred to by its connection ("Customer A
  12345"). With one, it reads "Autotask 12345" as before.

What slice 4a changes for people:

- Each live connection's card has a **Mapping** panel. It lists every status and priority the PSA
  sends, what each becomes in the portal, how many tickets hold it, and - for a status nothing maps
  - that those tickets are being counted as open work (or finished, where the PSA's own word says
  so). In production on 6 Oct 2026 every status and priority that tickets hold is mapped, on both
  connections (137 Autotask tickets, 13 ConnectWise; read from the database, not assumed), so the
  panel will report no unmapped ticket there today. It is there for the next value a PSA adds.
- It says which statuses the portal can be set to that the PSA has no counterpart for, before
  someone tries to set one and is refused.
- After mapping a value, **Apply the mapping to tickets already here** re-maps the imported tickets
  that still show the PSA's own word. It does not touch a status someone set in the portal, and
  sends nothing to the PSA.
- The add-connection wizard shows a sample of real tickets with what the rules would make of them.

What slice 4b changes for people:

- The Field Mapping page has a new section, **What the PSA sends**, for statuses and priorities.
  It lists every value the PSA sends with the tickets that hold it, and each can be given the
  portal value it becomes. Several PSA statuses can become the same portal status.
- It can be searched, filtered to what is unmapped, and it suggests the values that are the same
  words as a portal value. Several values can be ticked and mapped at once.
- Nothing is saved as it is chosen. Changes are listed, with what each value was mapped to before,
  and saved together.
- The history records who changed what, on which connection, from what to what.

What slice 4c changes for people:

- A PSA login can be marked to be left alone, from Users, Import from PSA, or from a connection's
  Mapping panel. It is then no longer counted or listed as still to link. Nothing about its tickets
  or time changes.

What the certification slice changes for people: nothing on screen. It is what lets the next PSA
be added without the work Autotask and ConnectWise needed, and holds a new connector to what those
two had to learn.

What the classification-mapping slice changes for people: nothing until an administrator writes a
rule. Field Mapping has a Classification tab for each connection, listing what its tickets are
filed under with the unmapped first. A rule written there gives tickets a work type and a
subcategory, and a category where the rule says one; a technician sees them on the ticket under
the PSA's own levels, or "Unmapped" where the connection has rules and none names the ticket.

What the ticket-reads slice changes for people: nothing they will see at today's size. The
dashboard, the ticket page's filters and the search answer the same as before; on a desk with
hundreds of thousands of tickets they answer in a fraction of a second instead of several.

What the classification slice changes for people: a technician opening a PSA ticket sees what
the PSA files it under (for ConnectWise: type, subtype, item; for Autotask: ticket type, issue
type, sub-issue type), where before there was only the category. Tickets already imported show
it after they are next read; "Re-sync all" brings them all up to date at once.

What the background-sync slice changes for people: "Sync now" and "Re-sync all" answer at once
instead of holding the page until the sync is over. The card says a sync has been asked for,
then *Syncing*, then what it did. If nothing takes a request for ten minutes the attention list
says the worker is not running.

What the volume slice changes for people: nothing they will notice today, at 150 tickets. The
numbers on every screen are the same. A first import of a large PSA no longer slows down as it
goes, and the Connections page, a connection's mapping and the ticket list stay quick with a
hundred connections and half a million tickets behind them. What is still slow at that size is
written down with what it needs, in [performance.md](performance.md).

Two decisions taken while building it, recorded because they differ from the first plan:

- **No unique index on notes or attachments by the PSA's id.** A reply written in the portal and a
  sync reading the same ticket can legitimately race; an index would turn a rare duplicate note into
  a failed reply that the PSA had already accepted, and the person would send it again. The lock
  closes the case that mattered: two syncs.
- **No register of unmapped values is stored.** The first plan had a table of them. A ticket
  already keeps the status and priority it arrived with beside the portal's, so what is unmapped is
  worked out from the tickets and the rules each time it is asked. A table would have had to be
  kept in step with every rule change, rollback and re-sync, and could be wrong; this cannot.
- **`app_users.ExternalTechnicianId` is not dropped yet.** Nothing reads it any more, but the
  previous version of the code selects it, and a zero-downtime deploy runs both versions for a
  moment. It goes in a later release.
