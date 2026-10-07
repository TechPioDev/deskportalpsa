# A client user with more than one company

Phase 9. A client user belongs to one company and sees that company's tickets. Some people
answer for more than one: the finance lead of a group, an IT manager of three sister firms.
The owner decided on 6 October 2026 (decision 1 in section 20a of the
[audit](PHASE9_PSA_INTEGRATION_ARCHITECTURE_AUDIT.md)) that such a person may see into a further
company, and only through an explicit mapping that somebody authorised made.

The code is `ClientCompanyAccess` (the grant), `ClientAccessResolver` (the one place a second
company can come from), `HttpActingCompany` (what the request names),
`ClientCompanyAccessService` (the desk's side) and, in the web app, the proxy, `actingCompany.ts`,
the company switcher and the Client access page. The tests are `ClientCompanyAccessTests` (on a
SQL translator, through the real rule for what a client may see), `actingCompany.unit.ts` and
`e2e/client-access.spec.ts`.

## The rule

**One request is about one company.** Every client read and write was already held to the
company in `ClientAccess`. That has not changed and nothing a client is answered is gathered
across companies. What is new is that the one company can be a company the person was given.

On every client request, `ClientAccessResolver` decides it:

1. **No company named:** the person's own.
2. **A company named:** their own, or one there is a grant for. The grant is read from the
   database on that request. One taken away a moment ago is gone for this one.
3. **Anything else is refused**, with 403 and the same words whether the company does not exist,
   belongs to another organization, or is simply not theirs. The answer does not say which.
4. **A company named and unreadable** (not an id, or two of them) is refused too. It is never
   taken to mean "none named": that would answer with the person's own company while they
   believe they are looking at another.
5. **A company given to be looked at** refuses every request that would change anything, here,
   before any service runs.

Every client endpoint already asks the resolver, so there is no second place to keep in step.

## Nothing is inferred

A grant is a row in `client_company_access` for one client user and one company, and that row is
the whole of the reason. Nothing reads an e-mail address, its domain or a company's name to
decide access. A test changes a person's address to one at the other company's domain and shows
it gives them nothing.

A row that somehow names a company of another organization (a restore, a script) gives nothing
either: the resolver and the list of a person's companies both require the company to be in the
person's own organization, named in the query and not left to the tenant filter.

## What a grant gives

The least it can, until more is said.

| | Not ticked (the default) | Ticked |
|---|---|---|
| **Sees every ticket** | Only the tickets that person raised in that company | Every ticket of the company a client may see |
| **May raise and reply** | The company is read-only to them: every change is refused | They may raise tickets there and reply on the ones they can see |

What a grant never gives:

- **The company's control panel.** In a company they were given, a person is never its
  administrator, whatever they are in their own. Its users, settings, announcements, branding,
  reports and knowledge base are refused, and the panel reports no sections. This needed a
  guard of its own: the sections of the control panel a non-administrator may manage are stored
  for the person and not company by company, so without it a grant to look at a second company
  would have let them edit that company's panel. A test fails if the guard is taken out.
- **Anything the desk keeps to itself.** The same rule for what a client may see applies: a PSA
  ticket, or a board published to clients. Work the desk recorded against the company on its own
  boards is not shown.
- **A device list for nothing.** A company's devices are listed for choosing one when raising a
  ticket. Someone who may only look is not given the list.

A ticket raised in a company the person was given is that company's ticket. The PSA is told
that company, the person's e-mail, and **no contact id**: their contact id in the PSA is a
contact of their own company there, and sending it would put another company's contact on the
ticket. A test fails if it is sent.

## Who may give one

Someone at the desk with `users.manage`, on **Client access** (under Users), one person and one
company at a time. A company of another organization, or a user of one, is not found. A person's
own company cannot be given: it is theirs already.

It is deliberately not `clientusers.manage`. That permission is in the built-in client
administrator role, and a client must never be able to give a company to anyone, themselves least
of all.

Each of these is in the audit log, with who did it, for whom, which company and what it gives:

| Action | When |
|---|---|
| `client.company_access.granted` | A company is given |
| `client.company_access.changed` | What a grant gives is changed, with before and after |
| `client.company_access.revoked` | A company is taken away |

Saying again what is already so writes nothing.

## How a client chooses

A client with more than one company has a company list in the header; nearly everyone has one
company and sees no such thing. Their own is first, and one they may only look at says "(view
only)".

- The choice is kept in a cookie, `desk_company`, so that every request the browser makes carries
  it, uploads and downloads included.
- The web app's proxy turns the cookie into the `X-Desk-Company` header, and only when it reads as
  one id. A header of that name sent by the browser itself is dropped, as every header not on the
  proxy's list is.
- The cookie is a choice and not a permission. A page script can read and set it, and setting it
  to a company that was never given gets a 403 on every request, from the rule above.
- Changing company loads the portal afresh, so that nothing read for the company just left is
  still on the screen or in memory.
- A company that was chosen and has since been taken away is let go of: the list of a person's
  companies is asked of the person, not of the company the request names, so it can still be read.

## Not in this slice

- **A combined view across companies.** A person looks at one company at a time. Lists, searches
  and counts are never added up across them; that is the property the rest rests on.
- **Notifications and e-mail** go by a ticket's requester and contact as before. Being given a
  company does not subscribe anyone to its tickets.
- **Approvals** are addressed by e-mail, as before. A person given a company may answer an
  approval there only where it is addressed to them, and only with "May raise and reply".
- **Per-company permissions finer than these two.** There is no "may raise but not reply", and no
  per-queue or per-category grant.
- **In the browser, as a real client.** The browser suite signs in as an administrator and cannot
  sign in as a client. What a grant allows and refuses is tested on the server, through the real
  rule; the browser tests show the desk's page sending the right requests and the switcher keeping
  the choice, over stood-in answers. The hop in between, the proxy turning the cookie into the
  header, is covered on each side (how the cookie is read, how the header is read) and not end to
  end. If it failed, the header would be missing and the person would be in their own company.

One migration, `ClientCompanyAccess`: one table, additive. No existing row is changed, and nobody
has a second company until one is given.
