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
| Confirmed and tentative work are always 0 | Nothing can be booked into people's time until work allocation ships | Expected in Phase 2 |
| Someone is missing from the technician search | The answer says why: without the skills asked for, not offered for planned work, no schedule, or no continuous slot long enough | Widen the time window or the dates, shorten the work, or check the skill rule (ALL by default) |
| Two free hours but a 2-hour task finds nothing | The hours are not continuous | Expected: one piece of work needs one slot |
| "Those dates have passed in ..." | The search date is before today in the organization's time zone | Search from the date the message names |
| A technician cannot add their own time away | `availability.manage` is not on the Technician role by default | Give the role that permission at scope Own |
| Team capacity and Find are not in the menu bar | The viewer can see only themselves | Expected for scope Own |
| A holiday shows but capacity is unchanged | Holidays are shown, not deducted | Record time away for whoever is off |
| "That is more than 1000 people..." | The request would work through too many people | Choose a team or a department |
