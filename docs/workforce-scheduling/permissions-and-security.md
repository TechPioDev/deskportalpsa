# Permissions and security

| Key | Scopes | Default holders | Allows |
|---|---|---|---|
| `schedule.view` | Own / Team / Department / All | Technician: Own. Manager: All. Administrator: All. | See schedules and skills of the people the scope reaches |
| `workforce.manage` | All | Administrator | Change schedules, breaks, time zones, "offered for planned work", the skill catalogue and who holds which skill |

How each scope reaches people:
- **Team** reaches people who share a team with the caller.
- **Department** reaches people who share a department with the caller.
- **Own** reaches the caller only.
- Every scope includes the caller.

No client role holds either key, and a pure client login's fixed claims don't include them (tested
per endpoint). The keys reach existing installations through the startup seeder, which only adds.

**Server-side enforcement:** the controller requires the claim, and the services check scope per
person through `WorkforceAccess`. Someone outside your scope, or in another organization, is "not
found", the same answer as a person who doesn't exist.

**Tenant isolation:**
- All new tables are tenant entities with the global query filter.
- Staff accounts aren't tenant-filtered by EF, so the organization is applied explicitly.
- A skill from another organization isn't found.
- The database layer refuses a cross-tenant write outright.

**Validation:** every rule is enforced on the server, and all problems are reported in one answer.
Request bodies are explicit records, never entities (no mass assignment).

**Audit:** every entry carries the request's correlation id. This phase made PIO fill that column for
every audit entry; it was always empty before.

| Area | Events |
|---|---|
| Schedules | `workforce.schedule.saved` (before and after summary), `workforce.schedule.removed`, `workforce.schedule.copied` |
| Planned work | `workforce.schedulable.changed` |
| Skills | `skill.created`, `skill.updated` (before/after), `skill.assigned`, `skill.level_changed`, `skill.removed` |

View as is read-only, so it can't change a schedule.
