# Internal Boards v1

Work the team does for itself, and alerts that arrive from monitoring, kept beside the PSA queues
without ever mixing with them.

## Why it exists

Most of what this team does never reaches a PSA, because most clients do not grant full access to
theirs. That work was invisible: raised in messages, remembered by the person doing it, and absent
from every figure management reads. Internal boards give it the same home as client work — one
ticket record, one place to log time, one set of productivity figures — while keeping the two
sides answerable separately.

## The shape of it

| Concept | What it is |
|---|---|
| **Origin** | Every ticket is PSA, Internal or Rmm. It decides whether the portal pushes anywhere, whether a client can see it, and which side of the reporting split it falls on. |
| **Board** | A place that is not a provider queue. Two kinds: the team's own work, and monitoring alerts. |
| **Prefix** | Each board's ticket numbers, e.g. `INT-000123`. Quoted between people, so it cannot change once tickets exist. |
| **Membership** | A board with no members belongs to the whole team. Naming members narrows it; the holder and the raiser keep sight of their own ticket regardless. |
| **Handover** | Every assignment records who passed it, to whom, who decided, and what they said. |

## The rules that must not bend

1. **A client never sees internal work.** Naming a client on an internal ticket records who the work
   was for; it does not publish it. Only an RMM board may be published, and only when someone turns
   that on deliberately. Asking for it on an internal board is ignored rather than obeyed.
2. **Nothing is pushed.** No provider call is made for an internal ticket's creation, notes, status
   or time. There is no provider to call.
3. **Internal tickets are not failures.** The "never reached the PSA" list and its critical alert
   cover tickets that belong in a PSA. An internal ticket is complete where it is.
4. **Client reports count client work only.** Business reviews and client report runs exclude
   internal tickets explicitly.
5. **Productivity is split, not blended.** Client work and internal work are reported side by side,
   per technician, for tickets and for hours. Internal work is self-raised, and a single blended
   number invites the question of who is marking their own homework; the split answers it, and the
   recorded assigner answers the rest.

## Permissions

| Permission | Who holds it | What it allows |
|---|---|---|
| `tickets.create` | Everyone on the desk | Raise a ticket on a board, assign it to anybody |
| `boards.manage` | Managers and administrators | Create, edit, open and close boards; set membership |

Deciding what boards exist is a lead's call. Handing work to a colleague is not, which is what a
team running day and night shifts needs.

## Switching it off

One setting covers the whole feature:

```
Features__InternalBoards=false
```

With it off the endpoints answer "not found" and the pages disappear from the interface. Nothing is
deleted: every board and ticket stays in the database and returns the moment it is switched back on.

## Rolling it back

| Step | Command |
|---|---|
| Code before the feature | `git checkout pre-internal-boards` |
| Schema | The migration `InternalBoards` is additive: it adds columns and three tables, and makes the ticket's PSA connection, provider and client company optional. Reverting it drops the boards, membership and handover tables, so export anything worth keeping first. |

The feature switch is the reversible option and should be tried first. The tag is there for the case
where the code itself must go.

## What is not built yet

- **RMM intake.** The board kind and the client-visible flag exist; the endpoint that receives a
  NinjaOne or Datto RMM webhook, deduplicates by alert id and closes the ticket when the alert
  clears does not. Both vendors publish outbound webhooks with custom payloads and an OAuth2 API.
- **Promoting an internal ticket into a PSA** once a client grants access.
- **Due dates, recurring internal tasks and checklists.**
