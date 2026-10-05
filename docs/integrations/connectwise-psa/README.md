# ConnectWise Manage Connector

ConnectWise Manage integration over REST API 3.0. Wave-1 reference connector (with Autotask).

## Connection setup
A `PsaConnection` with `Provider = ConnectWisePsa` and `ApiEndpoint` set to the instance API base
(e.g. `https://api-na.myconnectwise.net/v4_6_release/apis/3.0/`). The address is checked when it is
saved: `https`, and not a private address unless the operator has allowed the host
(`Connectors:AllowedHosts`), which is how a self-hosted instance on a private network is connected.
The credential fields required are:

| Key | Value |
|---|---|
| `CompanyId` | ConnectWise company identifier |
| `PublicKey` | API member public key |
| `PrivateKey` | API member private key |
| `ClientId` | Developer `clientId` GUID |
| `WebhookSecret` | HMAC secret for inbound deliveries. Without one, no delivery is accepted |

Auth is HTTP Basic (`base64(CompanyId+PublicKey:PrivateKey)`) plus the `clientId` header.

## Terminology mapping (portal ⇄ ConnectWise)
| Portal | ConnectWise |
|---|---|
| Company | Company |
| Queue / Board | **Service Board** |
| Technician | **Member** |
| Category | **Type** |
| Ticket title | **summary** (capped at 100 chars) |
| Status / Priority | nested `{id, name}` references |

## Field semantics & limitations
- References are nested objects; the connector sends `{id}` when the mapped value is numeric,
  otherwise `{name}`. Production mappings supply ids.
- **Updates are JSON-Patch** operations replacing whole reference objects.
- Public vs internal notes use `internalAnalysisFlag` (public notes are not flagged internal);
  internal notes are never mirrored to the portal.
- List endpoints return **bare JSON arrays** (no envelope) — different from Autotask.
- Supports **outbound webhooks (callbacks)**, which Autotask does not — captured in the capability
  matrix so the UI/sync treat the providers differently rather than assuming parity. Inbound
  deliveries currently use the portal's own signed format (`X-Timestamp`, and `X-Signature` = hex
  HMAC-SHA256 of `"{timestamp}.{body}"`), not ConnectWise's callback format: polling is how changes
  arrive today, and native callbacks are a later Phase 9 slice.
- **A ticket carries its board's id beside the name** (`QueueOrBoardId`). A connection's board
  limit holds ids, and is checked against the id.

## Error mapping
401 → Authentication · 403 → PermissionDenied · 404 → NotFound · 429 → RateLimited (honours
`Retry-After`) · 5xx → ProviderError · timeout → Timeout · other 4xx → InvalidRequest.

## Certification
Passes the shared `ConnectorCertificationSuite` end-to-end against an in-memory `FakeConnectWiseServer`,
and the cross-provider normalization tests confirm it yields the same `UnifiedTicket` shape as
Autotask. Status: *Ready for Integration Testing* (needs a live instance before production).
