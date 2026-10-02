# Permissions and security

| Key | Scopes | Default holders | Allows |
|---|---|---|---|
| `schedule.view` | Own / Team / Department / All | Technician: Own. Manager: All. Administrator: All. | See schedules, skills, capacity, free time and exceptions of the people the scope reaches; search and conflict-check among them |
| `workforce.manage` | All | Administrator | Change schedules, breaks, time zones, "offered for planned work", the skill catalogue and who holds which skill |
| `availability.manage` (Phase 2) | Own / Team / Department / All | Manager: All. Administrator: All. Technician: none. | Record time unavailable and additional availability for the people the scope reaches |

No separate capacity permission was added: capacity is what a schedule means on a date, so it
follows `schedule.view` and its scope. `availability.manage` is separate because recording time
away is a different right from seeing it. To let technicians record their own, give the Technician
role `availability.manage` at scope **Own**.

How each scope reaches people:
- **Team** reaches people who share a team with the caller.
- **Department** reaches people who share a department with the caller.
- **Own** reaches the caller only.
- Every scope includes the caller.

No client role holds any of these keys, and a pure client login's fixed claims don't include them
(tested per endpoint). The keys reach existing installations through the startup seeder, which only
adds.

## Internal only: client isolation

Client ticketing and workforce scheduling are separate security domains. A ticket may be visible to
a client; its planning never becomes visible because of that.

| Client attempt | Result | Proven by |
|---|---|---|
| Any `api/workforce/*` route: capacity, free slots, team capacity, the technician search, skills, exceptions, conflicts | Refused (403): no client role or client login holds `schedule.view` | `No_client_account_can_reach_any_workforce_endpoint` walks every controller under `api/workforce` by route, so a controller added later is covered |
| A guessed person or exception id | Refused before the id is looked at | Same |
| A workforce permission on a client role | Not possible by default | `No_client_role_or_client_login_holds_a_workforce_permission` |
| Seeing planning through their own ticket | The ticket and client-portal response shapes carry no workforce field | `Nothing_a_client_can_receive_carries_workforce_planning` fails if one is ever added |
| A signed-in account without the permission opening the pages by address | No menu entry; pages show an error; every endpoint answers 403 | `e2e/workforce-capacity.spec.ts` |

A second line of defence: every workforce action also requires a **staff** user id, so a principal
that is not a staff account is refused even if it somehow held the claim.

**Server-side enforcement:** the controller requires the claim, and the services check scope per
person through `WorkforceAccess`. Someone outside your scope, or in another organization, is "not
found", the same answer as a person who doesn't exist.

**Tenant isolation:**
- Another organization's person is "not found" for capacity, conflicts, and listing, adding,
  changing or removing an exception: the same words as for an id that names nobody, so nothing can
  be learned by probing. Naming them in a search finds nobody and counts nobody. A skill from
  another organization is refused with the same words as one that does not exist.
- An exception is looked up through the person it belongs to: another person's exception id on
  this person's route is "not found".
- All new tables are tenant entities with the global query filter.
- Staff accounts aren't tenant-filtered by EF, so the organization is applied explicitly.
- A skill from another organization isn't found.
- The database layer refuses a cross-tenant write outright.

**Validation:** every rule is enforced on the server, and all problems are reported in one answer.
Request bodies are explicit records, never entities (no mass assignment). A filter value that is
not a valid id is refused rather than ignored, so a malformed filter can never return more people
than were asked for. Notes are plain text: stored as typed and shown as text, never as markup.

**Limits against expensive requests:** 31 days of capacity, 14 days of search, 1,000 people, 200
matches, 20 skills, work of 5 minutes to 12 hours. The API's per-user and per-organization rate
limits apply as everywhere else. Nothing is cached, so there is no cache to leak between tenants.

**When, not why.** Everyone who may see a person's capacity sees *when* they are unavailable;
planning needs that. The reason ("Sick"), the note ("Dentist") and who recorded it are personal, so
the API returns them only to the person themselves and to whoever's `availability.manage` scope
reaches them. For anyone else those fields are null on every route that carries an exception
(person capacity, team capacity, the exception list).

**Error messages** never say whether a person exists in another organization or which rule kept
someone out of view; conflicts never carry a ticket's title, client or number
([conflicts.md](conflicts.md)).

**Audit:** every entry carries the request's correlation id. This phase made PIO fill that column for
every audit entry; it was always empty before.

| Area | Events |
|---|---|
| Schedules | `workforce.schedule.saved` (before and after summary), `workforce.schedule.removed`, `workforce.schedule.copied` |
| Planned work | `workforce.schedulable.changed` |
| Skills | `skill.created`, `skill.updated` (before/after), `skill.assigned`, `skill.level_changed`, `skill.removed` |
| Capacity exceptions | `workforce.exception.added`, `workforce.exception.updated` (before/after), `workforce.exception.removed` |

View as is read-only, so it can't change a schedule.
