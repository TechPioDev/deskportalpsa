# Autotask PSA Connector

Datto Autotask PSA integration over the REST API v1.0. Wave-1 reference connector.

## Connection setup
A `PsaConnection` with `Provider = AutotaskPsa` and `ApiEndpoint` set to the account's **zone base
URL** (e.g. `https://webservices2.autotask.net/atservicesrest/`). The address is checked when it is
saved: it must be `https` and on `autotask.net`, because the credentials below are sent to it on
every call. Credentials are encrypted at rest and referenced by `CredentialSecretRef`; the
credential fields required are:

| Key | Value |
|---|---|
| `ApiIntegrationCode` | API tracking identifier |
| `UserName` | API user (resource) name |
| `Secret` | API user secret |
| `WebhookSecret` | HMAC secret for inbound deliveries. Without one, no delivery is accepted |

The factory reads these from the secret store and configures an HttpClient; raw secrets never touch
the database or logs.

## Capabilities
Create/update tickets, public + internal notes, attachments, time entries, SLA data, custom fields,
companies/contacts/technicians, queues, incremental sync. Max page size 500.

Inbound deliveries use the portal's own signed format (`X-Timestamp`, and `X-Signature` = hex
HMAC-SHA256 of `"{timestamp}.{body}"`), not Autotask's webhook format. Polling is how changes
arrive today; Autotask-native webhooks are a later Phase 9 slice.

## Field semantics & limitations
- **Statuses / priorities / queues are numeric picklist ids.** The connector transmits values
  verbatim; portal ⇄ Autotask translation is the platform mapping engine's job, discovered live via
  `Tickets/entityInformation/fields`.
- **Notes use a numeric `publish` flag.** Public (client-visible) notes are written with
  `PublicPublishValue` (default 1); internal notes use `InternalPublishValue` (2) and are never
  mirrored to the portal.
- **No native create-idempotency.** Autotask cannot dedupe by an arbitrary key, so duplicate-create
  protection is enforced at the platform layer (sync-event idempotency), not in the connector.
- **What a ticket is filed under is read as words.** `ticketType`, `issueType` and `subIssueType`
  are picklist ids, like a status, and reach the portal as their labels (an id no longer in the
  list stays as the id). A sub-issue belongs to an issue, and the same label turns up under more
  than one; each has its own id. Sent on create as ids: Autotask refuses words for them.
- **A ticket carries its queue's id beside the name** (`QueueOrBoardId`). A connection's queue
  limit holds ids, and is checked against the id.
- **The tenant-wide attachment sweep reads every page** (500 a page, up to 20 pages a run) and
  reports whether its list is complete. Stored files are removed only when a complete list no
  longer contains them.
- **Query payloads are PascalCase** (`MaxRecords`, `Filter`) — the connector overrides the default
  camelCase JSON policy to match Autotask.

## Error mapping
401 → Authentication · 403 → PermissionDenied · 404 → NotFound · 429 → RateLimited (honours
`Retry-After`, as seconds or as a time) · 5xx → ProviderError · timeout → Timeout · other 4xx → InvalidRequest.

## Calls
Paced at 120 requests a minute once a burst is spent; each attempt bounded at 60 seconds; a query
(a POST, marked as a read) repeated on a passing fault, a write never. Every list is read to its
end. A retried time entry is first looked for in Autotask, matched on the resource, hours, charge,
summary notes (including the placeholder sent for empty notes) and a start no earlier than the
first attempt allows. See [provider-calls.md](../provider-calls.md).

## Certification
The connector passes the shared connector certification suite (`ConnectorCertificationSuite`),
exercised end-to-end against an in-memory fake Autotask server (`FakeAutotaskServer`). A live-sandbox
integration pass is still required before production (status: *Ready for Integration Testing*).
