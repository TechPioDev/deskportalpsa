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
| Team schedule, Team capacity and Find are not in the menu bar | The viewer can see only themselves | Expected for scope Own; Team schedule still opens by address, with one row |
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
| Team schedule: blocks cannot be dragged, or have no resize handle | The viewer may neither move nor give away that work (`canEdit` and `canReassign` false): a technician's fixed or other people's work, or a person outside the `schedule.manage` scope; resizing also needs `canEdit`; phones never drag | Open the block: the drawer offers what is allowed, or says "You cannot change this work. Ask whoever planned it." |
| Team schedule: a block jumped back after a drop | The server refused the move: a conflict, a stale version or a rule; the notice or the "Conflict detected" dialog says which | Pick another time, give a reason of 5+ characters if the dialog is offered, or reload |
| "This plan changed since the screen loaded. Reloading it." | A drop or resize carried a stale version; the board reloads itself | Make the change again |
| "Only someone who schedules others can give work to someone else." (on a drop) | A block was dropped on another person's row by someone whose `schedule.manage` is Own | Move it within your own row, or ask a scheduler |
| "This work is fixed in place. Ask whoever planned it to move it." (on a drop) | The person it is planned for tried to move fixed work | Ask whoever planned it |
| "Ask for at most 14 days at a time." | A request for more than two weeks of the team scheduler (`MaxTeamRangeDays`); the screen asks for one day or seven | Stay within 14 days |
| "Nobody matches these filters. Choose another team or clear the skills." | No visible person in the team, department or skills asked for; a team the viewer cannot see answers the same | Clear the filters |
| Unscheduled work is empty on the team schedule | Nothing open is held by, or routed to a team of, the people shown, or all of it is in someone's future plan | "Nothing is waiting: everything open is planned." is the normal state; clear the team and skill filters |
| A person is not a row on the team schedule | Outside the `schedule.view` scope, inactive, excluded by the team / department / skill filter, or hidden by the search | Widen the scope, reactivate, clear the filters |
| A block's time in its panel is not where it sits on the axis | The person works in another zone: the axis is in the organization's zone, the panel in theirs (named) | Expected; the row shows "· {zone}" |
| "This work is not pencilled in." / "This work is not committed." | Confirm on work that is not tentative; Pencil in on work that is not committed | Nothing to do |
| "Only someone who schedules others can pencil committed work back in." | The person it is planned for tried to take committed work back to tentative | Ask a scheduler, or take it out of the plan |
| "The plan changed since the preview. Review the new proposal." | The person's time changed between Calculate and Confirm | The dialog shows the new proposal; confirm it or change the window |
| "Only 7h of 10h fits in the window; 3h remains unallocated." | Less confirmed free time in the window than the effort; nothing is over-booked | Widen the window, plan the rest elsewhere, or correct the estimate |
| "No single free period of 5h in the window; the longest is 4h." | Continuous work, no stretch long enough | Tick *May be split*, widen the window, or pick someone else |
| A tentative block has no Confirm | `canConfirm` is false: fixed work scheduled for the viewer, or outside their `schedule.manage` scope | Ask whoever planned it |
| The queue says *Not enough capacity before the due date* | The holder's confirmed free time on the days up to the due date is less than the remaining effort | The figure beside the reason is the free time; plan it for someone else, split it, or accept the fact |
| "You already have active work on …" | One clock runs at a time | Return, or pause / stop the current one from the dialog |
| "This work changed since the screen loaded. Reload and try again." (a clock) | Another tab or device changed the session | Reload |
| The clock stopped but no time entry appeared | Under a minute, or discarded | Add the time by hand if it was real |
| "Not in the PSA yet" on My day | The entry's push failed or is pending | Retry from the ticket's time panel |
| "Say why the time is being changed (at least 5 characters)." | A lead changing someone else's time without a reason | Give the reason |
| Capacity utilization reads low on the analytics | The period has not ended, or people have no schedule (the notes say which) | Choose a finished period; set the schedules |
| "Work you cannot open" on an analytics table | The viewer may not open that ticket | Expected: the time counts, the ticket's identity does not travel |
| The analytics export answers 403 | The caller lacks `workforce.analytics.export` | An administrator grants it on the role |
| The forecast's capacity is 0 or low | People have no working schedule, or are not offered for planned work | Set the schedules; the Data and integrations tab says how many |
| Open work is missing from the forecast | Nobody holds it in the portal (a PSA ticket held only by a PSA login) | Link the PSA login to the person, or assign the work; the Data tab counts it |
| "Unestimated" is high and "Estimated unscheduled" is 0 | Nobody has sized the work | Set effort from the planning queue or the ticket's Planned work panel |
| A quality signal says Not available | The completed work carries no such data, or nothing records it | Read "What it rests on" beside it |
| A report has no Export buttons | The viewer lacks `workforce.analytics.export` | An administrator grants it on the role |

More in [planned-work.md](planned-work.md#troubleshooting),
[team-scheduler.md](team-scheduler.md#troubleshooting),
[advanced-planning.md](advanced-planning.md#troubleshooting),
[work-execution.md](work-execution.md#troubleshooting),
[analytics.md](analytics.md#troubleshooting),
[insights.md](insights.md#troubleshooting) and
[reports.md](reports.md#troubleshooting).
