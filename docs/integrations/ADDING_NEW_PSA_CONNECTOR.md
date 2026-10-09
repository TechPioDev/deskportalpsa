# Adding a new PSA connector

How to make a PSA the portal can connect to, from an empty folder to a connection an administrator
can add in the wizard. Written from the two connectors that exist (Autotask and ConnectWise) and
the framework Phase 9 built round them. Where something still has to be changed outside the
connector, this says so and says where: nothing here is "should work".

A PSA the portal already has a name for (`ProviderType`, thirteen of them without a connector
today) is shown in the wizard as **Coming soon**. It stops being that the moment a connector
factory for it is registered: there is no list of "available" providers to edit.

## What you write

| # | What | Where |
|---|---|---|
| 1 | The connector | `packages/connectors/<Provider>/` |
| 2 | Its factory, with the descriptor and the account key | `packages/infrastructure/Connectors/<Provider>ConnectorFactory.cs` |
| 3 | Two lines of registration | `packages/infrastructure/DependencyInjection.cs` |
| 4 | A fake of the provider's API, and the certification | `tests/unit/Certification/` |
| 5 | The permissions it needs, and its README | `docs/integrations/` |

And you visit the places in [What is still provider-specific](#what-is-still-provider-specific).

## 1. The connector

One class implementing `IServiceManagementConnector` (`packages/psa-core/Contracts`). It is bound
to one connection, is given an `HttpClient` already pointed at that connection's address, and
never sees a secret it was not constructed with.

### What it has to do

- **Say only what it does.** `GetCapabilitiesAsync` is read by the screens, which offer what it
  claims. A flag is true when the connector does the thing today, not when the PSA could. Both
  real connectors claimed inbound webhooks while understanding only the portal's own signed frame,
  which neither PSA can send.
- **What it does not do must still be safe to ask for.** A method behind a capability it does not
  claim returns an empty answer, never an exception (`GetCustomFieldsAsync`, `GetHolidaysAsync`,
  `GetAgreementsAsync`).
- **Read to the end.** Every list is paged until the provider says there is no more. A list too
  long to finish is an error (`ProviderError`), never a shorter list: a caller handed part of a
  ticket's notes as though it were all of them deletes the ones it was not shown.
  `GetTicketsAsync` returns one page and a cursor; the sync asks for the next.
- **Send the filter it is given.** `TicketFilter` carries the connection's scope (companies, queues
  or boards, resources, open only, active within N days) and `ModifiedSince`. Push down what the
  provider can express. Build the filter in one place and use it for the read and for the count,
  so the two cannot differ.
- **Count, or say it cannot.** `CountTicketsAsync` returns the number the same filter would read,
  asked of the provider as a count, or `null` where the provider has no such call. It never returns
  a number of its own making. (Default: `null`.)
- **Give each option both of its values.** An `ExternalFieldOption` has a `Value` (what is sent TO
  the provider: a filter, an assignment) and a `SyncValue` (what a ticket ARRIVES with). They
  differ more often than not - a queue is filtered by id and reported by name. Mapping rules are
  compared against `SyncValue`. One string doing both jobs failed silently on both connectors.
- **Never repeat a write.** A read may be repeated after a timeout; a write may not, because the
  provider may have done it. Mark what is safe: a `GET` is taken as a read; a `POST` that is a
  query (Autotask's are) calls `request.AsRead()`.
- **Find before sending twice.** Where the provider lets time entries be looked up,
  `FindTimeEntryAsync` finds the one a lost reply may have created. Without it, a retried entry is
  not sent at all. (Default: `null`, "cannot look".)
- **Translate failures.** Every failure leaves the connector as a `ConnectorException` with a kind:

  | Kind | When | Repeated? |
  |---|---|---|
  | `Authentication` | The provider rejected the credentials | No. The connection stops being polled until new ones pass a test |
  | `PermissionDenied` | Signed in, not allowed | No. The record waits for a person |
  | `RateLimited` | Told to slow down. Set `RetryAfter` from the provider's header, seconds or a date | Yes |
  | `Timeout` | No answer, or the network failed | Yes, for reads |
  | `NotFound`, `InvalidRequest` | The provider will give the same answer next time | No |
  | `ProviderError` | The provider's own 5xx; or a list too long to finish | Yes, for reads |

  The message is what an administrator reads. It must never contain a credential, a header or a
  request body.

### What it must not do

- Open its own `HttpClient`, follow a redirect, or resolve a host itself. The client it is given
  is guarded (below).
- Wait, sleep or retry. Pacing, the time limit on each attempt and retries belong to the layer
  under it (`ProviderCallHandler`), per connection.
- Delete anything in the provider. Nothing in the portal asks it to.
- Log a value. Field **names** may be logged; what a customer wrote may not.

## 2. The factory

`IConnectorFactory`, in `packages/infrastructure/Connectors`. Four members matter.

```csharp
public ProviderDescriptor Descriptor { get; } = new(
    ProviderType.HaloPsa, "HaloPSA",
    EndpointExample: "https://yourcompany.halopsa.com/api/",
    EndpointHint: "Your HaloPSA address, ending in /api/.",
    [
        new CredentialField("ClientId", "Client ID", Secret: false),
        new CredentialField("ClientSecret", "Client secret", Secret: true, "From the API application you registered."),
    ]);
```

- **`Descriptor`** is the form. The wizard and the edit form are built from it: the name, the
  example address, each credential's label, which are typed as secrets. There is no page to edit.
- **`CreateAsync(connectionId)`** builds a connector from the stored credentials.
- **`CreateWithAsync(connection, credentials)`** builds one from an address and credentials as
  GIVEN, without reading or writing the secret store. It is how a connection is tested before it
  is saved, and how new credentials are tried before they replace working ones. Share one private
  `Build` with `CreateAsync`.
- **`AccountKey(endpoint, credentials)`** names the PSA account a connection reaches: the host and
  the provider's own account name (Autotask: the API user; ConnectWise: the company id), lower-case.
  It is hashed and kept beside the connection, and is what stops one account being connected
  twice. No secret goes in it. Return `null` only where it genuinely cannot be said.

Build the HTTP client through `ProviderHttpClients`, not by hand:

```csharp
var http = clients.For("halopsa", connection.Id, config.BaseUrl, HaloConnector.RequestsPerMinute);
```

That gives the connection its own pace (`RequestsPerMinute`, with a burst of two minutes'
allowance), a 60-second limit on each attempt, three attempts for what is safe to repeat, and
`Retry-After` honoured in both of its forms. Set `RequestsPerMinute` from the provider's published
limit, below it.

## 3. Registration

In `DependencyInjection.cs`:

```csharp
services.AddScoped<IConnectorFactory, HaloConnectorFactory>();
```

and add the client's name to the list that gives connector clients their guarded transport:

```csharp
foreach (var client in new[] { "autotask", "connectwise", "halopsa" })
```

**Do not skip the second line.** It is what pins the client to the addresses it resolved, refuses
private and reserved ranges, and turns redirects off. A client whose name is not in that list is an
ordinary one, and an administrator could point the worker at an internal address with it.
`ConnectorEndpointTests.The_client_a_connector_is_actually_given_is_guarded_too` holds this for the
names it is given: add yours to it.

If the provider's API lives under one known domain, add that rule to
`ConnectorEndpointPolicy.Validate` beside Autotask's (`autotask.net`). An address is checked when
it is saved: https only, no credentials in it, no private host.

That is all the server needs. The catalog, the wizard, the test matrix, discovery, mapping health,
the preview and the sync are written against the contract.

## 4. The fake and the certification

Write a fake of the provider's API as an `HttpMessageHandler` (`FakeAutotaskServer`,
`FakeConnectWiseServer`). Make it **as strict as the real thing**: honour page sizes and return a
next-page link, apply every filter the connector sends, require the authentication the provider
requires. Each of the fakes has, at some point, been more forgiving than the API it stood for, and
each time it hid a defect that reached production: a list that was only ever one page, a filter
that matched everything, a time entry that was never stored.

Then two certifications.

**The connector.** Subclass `ConnectorCertificationSuite` and supply a healthy connector, a failing
one for each kind of failure, the id of an organization the fake holds, and the webhook secret. The
suite then holds the connector to the contract: capabilities, the kinds of failure, directory,
ticket create/read/update, notes with their visibility, attachments and the sweep, field options,
paging to the last ticket, incremental reads, a count that agrees with the read, safety of what is
not claimed, and the webhook frame (valid, tampered, stale, replayed, no secret, another secret).
It runs today against Autotask, ConnectWise and the mock: 26 contract tests each.

**The factory.** Add yours to `ConnectorFactoryCertificationTests.Factories`. It holds the
descriptor to being a form someone can fill in (the fields it asks for are the fields it reads)
and the account key to naming the account and nothing secret.

Then write the tests only your provider needs, in its own certification class: its payload quirks,
its error bodies, anything the fake had to learn.

`MockConnector` is not a template for a real connector. It is a PSA with no network, for tests of
everything above the connector.

## 5. Documents

- A section in `PSA_CONNECTOR_REQUIRED_PERMISSIONS.md`: every route the connector calls, read from
  the code, with what stops working without each. Ask for nothing the connector does not call.
- `docs/integrations/<provider>/README.md`: what the provider calls things, its limits, its quirks.

## What is still provider-specific

Places outside the connector that branch on the provider today. A new connector works without
touching most of them; each line says what you get if you do not.

| Where | What | If you leave it |
|---|---|---|
| `ProviderNames` (`psa-core`) | The short name ("Autotask") and the two letters on a connection's tile | The enum's own name, and its first two letters |
| `PsaTicketLink` (`application/Tickets`) | The address of a ticket in the provider's own screen | No "open in the PSA" link |
| `ConnectionAdminService.TenantIdentifierFor` | ConnectWise's company id doubles as the tenant identifier, for deep links | The optional identifier stays as typed |
| `ConnectorEndpointPolicy` | Autotask's "must be under autotask.net" | Only the general address rules apply |
| `AuthorBackfill` | A one-off repair that skips ConnectWise | Nothing: it is history |
| `SyncSettings.tsx` (web) | Words: "queue" or "service board", "resource" or "member", the names of the ticket-type levels; and Autotask's rule that a time entry needs a technician and a role they hold | Autotask's words are shown |
| `tickets/[id]/page.tsx` (web) | The provider's name in two sentences | "the PSA" |
| `mappings/page.tsx` (web) | A curated list of likely values for the older rows | The values discovered from the connection |

These are what is left of finding R7 in the Phase 9 audit. They are wording and convenience, not
behaviour: none of them decides what is synced or who may see it.

## Before you call it done

- [ ] The connector's certification passes, and the factory's.
- [ ] The fake refuses what the provider refuses.
- [ ] `RequestsPerMinute` is set from the provider's published limit.
- [ ] The client's name is in the guarded list, and in `ConnectorEndpointTests`.
- [ ] Every capability flag is true because of code you can point to.
- [ ] Every option has the right `Value` and `SyncValue`, checked against a real ticket.
- [ ] A connection added in the wizard against the provider's sandbox: the test matrix passes,
      discovery lists what the PSA lists, the preview's count matches the provider's own, and the
      first sync imports what the preview said it would.
- [ ] A status changed in the portal arrives in the provider, and one changed in the provider
      arrives in the portal.
- [ ] The permissions document says what the API account needs, and it was tested with exactly that.
- [ ] No secret, header or body in any log line or error message. Search the logs of the run.
