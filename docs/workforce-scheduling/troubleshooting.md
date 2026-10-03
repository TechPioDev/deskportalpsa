# Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| No "Workforce" menu; routes answer 404 | Module switched off | Set `FEATURES_WORKFORCE=true` in `.env.prod` (it becomes `Features__Workforce` for the API) and redeploy |
| A technician sees only themselves | `schedule.view` scope is Own (default for technicians) | Expected. Widen the scope in Roles & Permissions if a role should see a team. |
| "...is not a time zone this system knows" | Not an IANA id on a Linux server | Pick from the list (it comes from the browser) |
| Times look shifted on a Windows dev machine | Invariant globalization: IANA zones resolve to UTC on Windows | Development only; the servers are Linux |
| "A schedule can start today ... or later" | Past days keep their schedule | Choose today or a later date |
| "Only a schedule that has not started yet can be removed" | That version is already in force | Save a new version from today instead |
| "...is retired. Reactivate it..." | Skill retired | Reactivate it in the Skills catalogue |
| Saving a schedule again on the day it starts gave a server error | A Phase 1 defect on real databases, fixed in Phase 2 | Update to the Phase 2 release |
| Capacity shows 0 for someone | No working schedule, a day the schedule does not work, or a full day unavailable | The Availability tab says which; set a schedule or check their time away |
| Tentative work is always 0 | Every allocation is confirmed work; there is no pencilled-in state yet | Expected in Phase 3 |
| Confirmed (Planned) is 0 although work is planned | The ticket is finished: the engine ignores a future allocation on a finished ticket at once | Expected; the release pass records it within 5 minutes |
| Someone is missing from the technician search | The answer says why: without the skills asked for, not offered for planned work, no schedule, or no continuous slot long enough | Widen the time window or the dates, shorten the work, or check the skill rule (ALL by default) |
| Two free hours but a 2-hour task finds nothing | The hours are not continuous | Expected: one piece of work needs one slot |
| "Those dates have passed in ..." | The search date is before today in the organization's time zone | Search from the date the message names |
| A technician cannot add their own time away | `availability.manage` is not on the Technician role by default | Give the role that permission at scope Own |
| Team capacity and Find are not in the menu bar | The viewer can see only themselves | Expected for scope Own |
| A holiday shows but capacity is unchanged | Holidays are shown, not deducted | Record time away for whoever is off |
| "That is more than 1000 people..." | The request would work through too many people | Choose a team or a department |
| "This plan changed since you opened it. Reload and try again." | The version sent with a move or reassignment is stale: someone changed that piece of work since the screen loaded | Reload and make the change again |
| "… cannot open this ticket: it is held by …. Hand it over to … first, or plan it for …" | Scheduling someone on a ticket they cannot see while someone else holds it in the portal | Hand the ticket over to them first, or plan it for the holder |
| "This time cannot be used: …" | A block (marked unavailable, or not offered for planned work); nobody can override it | Change the time away or switch "offered for planned work" on, or choose another time |
| "This time is no longer available: …" | An overridable conflict and the caller holds no `schedule.override` | Pick a free window, or ask someone who can override |
| "This time has a conflict: … Give a reason to override it." | The caller may override but gave no reason, or one under 5 characters | Type a reason; the button becomes "Override and save" |
| A resolved or closed ticket still shows in a future plan | The worker releases finished work every 5 minutes | Wait up to 5 minutes; capacity already ignores it, and the row says "Ticket finished" |
| No "Add to plan" / "Plan work" button | The role has no `schedule.manage` | Technicians get it at scope Own by default; give it to the role |
| A technician cannot plan for a colleague, fix work, take scheduled work out or give it away | `schedule.manage` is Own | A wider scope (Team, Department or All) on their role |
| "Work can be planned from yesterday onwards, not earlier." | The start is more than 24 hours in the past | Plan from yesterday on; past time is logged on the ticket |
| The Plan tab is not on the Users → person page | Only the Workforce → person page carries it | Open the person from Workforce |

More in [planned-work.md](planned-work.md#troubleshooting).
