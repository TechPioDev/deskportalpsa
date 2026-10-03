import os
from reportlab.lib.pagesizes import A4
from reportlab.lib.units import mm
from reportlab.lib import colors
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib.enums import TA_LEFT
from reportlab.platypus import (
    SimpleDocTemplate, Paragraph, Spacer, PageBreak, ListFlowable, ListItem, Table, TableStyle, HRFlowable
)

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(REPO, "apps", "web", "public", "user-guide.pdf")
os.makedirs(os.path.dirname(OUT), exist_ok=True)

BRAND = colors.HexColor("#2563eb")
INK = colors.HexColor("#0f172a")
MUTED = colors.HexColor("#475569")
FAINT = colors.HexColor("#64748b")
LINE = colors.HexColor("#e2e8f0")

styles = getSampleStyleSheet()

def S(name, **kw):
    return ParagraphStyle(name, parent=styles["Normal"], **kw)

title_s   = S("t", fontName="Helvetica-Bold", fontSize=34, textColor=BRAND, leading=38, spaceAfter=6)
sub_s     = S("sub", fontName="Helvetica", fontSize=13, textColor=MUTED, leading=18, spaceAfter=2)
h1_s      = S("h1", fontName="Helvetica-Bold", fontSize=17, textColor=INK, leading=21, spaceBefore=16, spaceAfter=6)
h2_s      = S("h2", fontName="Helvetica-Bold", fontSize=12.5, textColor=INK, leading=16, spaceBefore=10, spaceAfter=3)
body_s    = S("b", fontName="Helvetica", fontSize=10.5, textColor=INK, leading=15.5, spaceAfter=6, alignment=TA_LEFT)
muted_s   = S("m", fontName="Helvetica", fontSize=9.5, textColor=MUTED, leading=13, spaceAfter=4)
bullet_s  = S("bl", fontName="Helvetica", fontSize=10.5, textColor=INK, leading=15)
eyebrow_s = S("ey", fontName="Helvetica-Bold", fontSize=9, textColor=BRAND, leading=12, spaceAfter=2)
note_s    = S("n", fontName="Helvetica", fontSize=9.5, textColor=MUTED, leading=13, spaceAfter=4,
              backColor=colors.HexColor("#f1f5f9"), borderPadding=6, leftIndent=2, rightIndent=2)

def bullets(items):
    return ListFlowable(
        [ListItem(Paragraph(t, bullet_s), leftIndent=10, value="•") for t in items],
        bulletType="bullet", start="•", leftIndent=14, spaceAfter=6,
    )

def hr():
    return HRFlowable(width="100%", thickness=0.7, color=LINE, spaceBefore=4, spaceAfter=8)

story = []

# ---- Cover ----
story += [Spacer(1, 40*mm)]
story += [Paragraph("DESK PORTAL", eyebrow_s)]
story += [Paragraph("User Guide", title_s)]
story += [Paragraph("A multi-tenant PSA ticket portal — for clients, technicians, managers, and administrators.", sub_s)]
story += [Spacer(1, 6)]
story += [hr()]
story += [Paragraph("Version 0.1.0", muted_s)]
story += [Paragraph("This guide covers everyday use of the portal: raising and following tickets, the productivity "
                    "dashboards, and administering PSA connections and field mappings.", body_s)]
story += [PageBreak()]

# ---- 1. Getting started ----
story += [Paragraph("1. Getting started", h1_s), hr()]
story += [Paragraph("Signing in", h2_s)]
story += [Paragraph("Open the portal and choose <b>Continue with SSO</b>. Authentication is handled by your "
                    "organization's identity provider — you'll be returned to the dashboard once signed in. Your name "
                    "and a <b>Sign out</b> option appear at the top right.", body_s)]
story += [Paragraph("Finding your way around", h2_s)]
story += [Paragraph("The left sidebar is your main navigation. On a phone it collapses into a scrollable bar at the top. "
                    "A light/dark theme toggle sits at the top right.", body_s)]
story += [bullets([
    "<b>Tickets</b> — raise and follow support requests.",
    "<b>Internal boards</b> — the work your team does for itself, and alerts from monitoring (staff).",
    "<b>Productivity, Technician hours, Client workload</b> — how the desk is doing (staff).",
    "<b>Scheduled reports</b> — reports that send themselves (staff).",
    "<b>PSA Connections, Field Mapping, Integration Health, Background Jobs, Audit Log</b> — administration.",
])]

# ---- 2. For clients ----
story += [Paragraph("2. Raising &amp; following tickets", h1_s), hr()]
story += [Paragraph("Create a ticket", h2_s)]
story += [Paragraph("Go to <b>Tickets → New ticket</b>. Give it a short title, choose a priority, and describe the "
                    "issue. On submit, the ticket is created in your provider's system (the PSA) and appears in your list.", body_s)]
story += [Paragraph("Track progress", h2_s)]
story += [bullets([
    "The <b>ticket list</b> shows status, priority, queue and when each was raised.",
    "Open a ticket to see its <b>public conversation</b> and add a reply.",
    "<b>Attach files</b> from the ticket detail — they are scanned for malware; executables are blocked; 25 MB max. "
    "Only clean files can be downloaded.",
    "<b>Notifications</b> lists recent activity on your tickets.",
])]
story += [Paragraph("Internal notes your support team writes are never shown in the portal — you only ever see public "
                    "replies.", note_s)]

# ---- 3. Working a ticket (staff) ----
story += [Paragraph("Rate a resolved ticket", h2_s)]
story += [Paragraph("When one of your tickets is resolved, the ticket page asks <b>How did we do?</b> Choose from "
                    "1 (very poor) to 5 (excellent) and, if you like, add a comment. You can change your rating for "
                    "30 days after the ticket was resolved. Your answer goes to the team that worked the ticket.", body_s)]

story += [Paragraph("Approve a request", h2_s)]
story += [Paragraph("If your company lists you as an approver, a request that needs your agreement - a licence, a "
                    "purchase, out-of-hours work - appears at the top of your Overview and Tickets pages. Read it, add "
                    "a comment if you like, and choose <b>Approve</b> or <b>Reject</b>. The team is told at once, and "
                    "your answer is recorded on the ticket with your name and the time. Your company administrator "
                    "keeps the approver list in the Control Panel, under Approvers.", body_s)]

story += [Paragraph("Your devices", h2_s)]
story += [Paragraph("Administrators see the company's devices under <b>Control Panel &gt; Accounts &amp; Devices</b>. "
                    "Devices your IT provider tracks in their PSA appear automatically and are refreshed once a day; "
                    "each shows its type, serial and warranty, and a warning when the warranty has ended or ends "
                    "within 60 days. Open a device to see every ticket raised about it. You can add notes to any "
                    "device, and add devices of your own by hand.", body_s)]
story += [Paragraph("When you raise a ticket, <b>Which device?</b> lets you say which of your company's devices it "
                    "is about - it goes to your IT team with the ticket.", body_s)]

story += [Paragraph("Help", h2_s)]
story += [Paragraph("<b>Help</b> in the menu has answers from your IT team and your company's own FAQ, grouped by "
                    "subject, with a search box. While you type a new ticket's title, matching articles appear under "
                    "it; if one fixes the problem, <b>This solved it</b> means you don't need to raise the ticket at all.", body_s)]

story += [Paragraph("3. Working a ticket (staff)", h1_s), hr()]
story += [Paragraph("Open a ticket and use the box at the top of the conversation. One action covers the three things "
                    "you usually do at once: say what happened, move the ticket on, and record your time.", body_s)]
story += [bullets([
    "<b>Internal note</b> is selected first, because most of what you write is for the team. It never reaches the client.",
    "<b>Public reply</b> appears only when the ticket has a contact who would actually receive it. On an internal "
    "board there is no client to reply to, so it is not offered at all.",
    "<b>Set status</b> changes the ticket as you post, rather than as a second step you might forget.",
    "<b>Log time</b> records hours against the ticket, with the reply they belong to. There is a timer if you prefer "
    "to start it and forget it.",
])]
story += [Paragraph("Assign or hand over", h2_s)]
story += [Paragraph("<b>Assign technician</b> sets who is working the ticket. On an internal board you can also leave a "
                    "<b>handover note</b> — what the next person should read first — which is kept with the handover, "
                    "so a night shift picks up where the day shift left off instead of guessing.", body_s)]
story += [Paragraph("You can also route a ticket to a <b>team</b> — Level 2, NOC — as well as to a person. A ticket can "
                    "sit with a team before anyone picks it up, and stays with the team once someone does.", body_s)]
story += [Paragraph("A ticket that came from a PSA also shows a link that opens it in that system, for the things the "
                    "portal deliberately does not duplicate.", note_s)]

story += [Paragraph("Follow a ticket", h2_s)]
story += [Paragraph("<b>Follow</b> puts a ticket in your own <b>Following</b> view without taking it on — for the ticket "
                    "you escalated, or the customer you look after. <b>Add somebody</b> does the same for a colleague. "
                    "Following never changes who is working the ticket.", body_s)]

story += [Paragraph("Tasks", h2_s)]
story += [Paragraph("Break a ticket into steps under <b>Tasks</b> — order the part, fit it, update the register. Tick each "
                    "one off as it is done; the portal records who ticked it and when. A ticket cannot be closed from "
                    "the portal while any of its tasks is still open. Tasks are for your team only and are never sent "
                    "to a PSA or shown to a client.", body_s)]

story += [Paragraph("Canned responses and formatting", h2_s)]
story += [bullets([
    "<b>Canned response</b> in the reply box inserts a saved reply, filled in with this ticket's number, "
    "customer, contact and your name. Edit it as you like before sending.",
    "The toolbar formats the text: bold, italic, lists, quotes, code, links and tables.",
    "Paste a screenshot straight into the reply box and it is attached to the reply.",
])]
story += [Paragraph("Some PSAs, Autotask among them, show formatting as the marks you typed rather than as formatting.", note_s)]

story += [Paragraph("Find a ticket", h2_s)]
story += [bullets([
    "The <b>search box</b> at the top of every page (Ctrl+/) finds ticket numbers, subjects, customers and, once you "
    "have typed four characters, words in the replies. Press Enter to see every match as a list.",
    "The <b>views</b> above the ticket list — Open, Mine, Unassigned, Overdue, Due soon (the next 8 hours), Following, "
    "Closed — are one click each. "
    "<b>Mine</b> includes tickets sitting with a team you are in.",
    "Set any filters you use often, then <b>Save this view</b>. Tick <b>Share with the team</b> to offer it to "
    "everyone; only you can change or delete it.",
])]

# ---- 4. Internal boards ----
story += [Paragraph("4. Internal boards (staff)", h1_s), hr()]
story += [Paragraph("Not every job comes from a PSA. <b>Internal boards</b> hold the work your team does for itself and "
                    "for each other: tasks you assign between shifts, project work, and anything a client has asked for "
                    "that never reached their PSA. Nothing on a board is sent to a PSA, and nothing on an internal board "
                    "is visible to a client.", body_s)]
story += [bullets([
    "<b>Raise a ticket</b> on a board and assign it to anyone on the team. Everybody can assign to everybody.",
    "Pick <b>what it is about</b> — a topic such as Patching or Access request — and the department, "
    "priority, assignee and due date that kind of work usually has are filled in for you. Change any "
    "of it: the topic is a shortcut, not a rule. Leads set the topics up under <b>Topics</b> on the board.",
    "Record <b>how it reached us</b> (phone, email, chat, someone at the desk, a meeting) so the question "
    "of where your work comes from can be answered later.",
    "A <b>due date</b> shows in the list, with overdue in red and anything due within eight hours marked soon.",
    "Each board has its own numbers, such as <b>INT-000123</b>, which is what people quote to each other.",
    "You can name the <b>client</b> a piece of work was for. It records who it was for — it does not show the ticket "
    "to them.",
    "A board with no members belongs to the whole team. Leads and administrators can narrow a board to named people.",
    "Creating and configuring boards is limited to leads and administrators; raising and assigning is not.",
])]
story += [Paragraph("SLA plans", h2_s)]
story += [Paragraph("Leads set up <b>SLA plans</b> under <b>Internal boards &rarr; SLA plans</b>: a first reply within so "
                    "many hours and resolution within so many, counted round the clock or in working hours only — including a night shift such as 22:00 to 06:00. Give a "
                    "plan to a topic, or make it a board's default, and each new ticket shows <b>First reply</b> and "
                    "<b>Resolution</b> dates, marked met, late or overdue. The board list shows <b>Reply by</b> until "
                    "somebody writes on the ticket.", body_s)]

story += [Paragraph("Recurring tickets", h2_s)]
story += [Paragraph("Under <b>Internal boards &rarr; Recurring</b>, leads schedule work that comes round: every day, every "
                    "weekday, a day of the week, or a day of the month, at an hour of your choosing. The ticket is raised "
                    "on its board with its checklist as tasks. While the last one is still open, the next is skipped "
                    "rather than piled up. <b>Raise now</b> tries it straight away without changing the schedule.", body_s)]

story += [Paragraph("Holidays and waiting", h2_s)]
story += [Paragraph("Add the desk's closed days under <b>SLA plans &rarr; Holidays</b>; working-hours plans skip them. "
                    "When a board ticket is set to <b>Waiting customer</b> or <b>On hold</b>, its SLA shows <b>Paused</b> and "
                    "the time is given back when it moves on, so it does not go overdue while the customer has it.", body_s)]

story += [Paragraph("Alerts from monitoring tools", h2_s)]
story += [Paragraph("A board of the <b>monitoring</b> kind can be fed by NinjaOne, Datto RMM or anything else that can "
                    "post JSON. Under <b>Internal boards &rarr; Monitoring tools</b>, connect a tool and copy the key it "
                    "shows you into that tool's webhook, along with the address on the same page.", body_s)]
story += [bullets([
    "The same alert arriving repeatedly updates one ticket rather than opening a pile of them.",
    "When the tool says the condition cleared, the ticket closes itself.",
    "If the same fault returns within a day, that ticket reopens instead of a second one appearing.",
    "The client the tool names is matched against your customer list; a name nobody recognises is reported back "
    "rather than guessed at.",
])]
story += [Paragraph("The key is shown once, when it is issued, because only a fingerprint of it is stored. If it is "
                    "lost or leaks, issue a new one — the old one stops working immediately.", note_s)]

story += [PageBreak()]

# ---- 5. Dashboards ----
story += [Paragraph("5. Productivity dashboards (staff)", h1_s), hr()]
story += [Paragraph("The <b>Productivity</b> page shows technician and team metrics: assigned, resolved, open and "
                    "overdue tickets, SLA compliance, average resolution time, and time worked. A configurable "
                    "<b>productivity score</b> combines several signals into a single number, with a breakdown per "
                    "component. Use <b>Export CSV</b> to download the team view.", body_s)]
story += [Paragraph("Customer satisfaction", h2_s)]
story += [Paragraph("<b>Satisfaction</b> shows how clients rated resolved tickets over any period: CSAT (the share "
                    "rated 4 or 5 out of 5), the spread of scores, figures per technician and per client, and every "
                    "comment and poor rating. A rating counts for whoever held the ticket when it was rated. Ratings "
                    "of 1 or 2 in the last week also appear on the needs-attention list, and each client's business "
                    "review shows their satisfaction for the quarter.", body_s)]

story += [Paragraph("Devices on tickets", h2_s)]
story += [Paragraph("The ticket page shows the device a ticket is about, with its serial and warranty. <b>Set device</b> "
                    "(or <b>Change</b>) picks one of the client's devices; on a PSA ticket the PSA is updated first. "
                    "A PSA ticket can only use devices the PSA knows - one added by hand in the portal is shown greyed "
                    "out with the reason. Monitoring alerts that name a device by its exact name are linked to it.", body_s)]

story += [Paragraph("Knowledge base", h2_s)]
story += [Paragraph("<b>Knowledge base</b> holds the team's articles. Each is <b>Staff only</b> (runbooks), for <b>All "
                    "clients</b>, or for <b>Chosen clients</b>, and stays a draft until published. Articles use the same "
                    "formatting as ticket notes, with a preview. Client articles appear on their Help page and are suggested "
                    "while they type a new ticket; the top of the page counts the tickets they avoided and which articles "
                    "helped most.", body_s)]

story += [Paragraph("The app and notifications", h2_s)]
story += [Paragraph("The portal installs like an app. On Android, open it in Chrome and choose <b>Install app</b> (or "
                    "<b>Add to Home screen</b>). Notifications work on Android phones and on computers; iPhone is not "
                    "supported yet. "
                    "Then, on your <b>Profile</b> page, press <b>Turn on for this device</b> and allow notifications. "
                    "You are told when a ticket is assigned to you (in the portal or in the PSA, once your PSA account "
                    "is linked), when a client replies on your ticket, and when your ticket is two hours from breaching "
                    "its SLA. Untick any of the three you do not want, press <b>Send a test</b> to check a device, and "
                    "remove old devices from the list.", body_s)]

story += [Paragraph("Client approvals", h2_s)]
story += [Paragraph("On a client's ticket, <b>Ask for approval</b> sends a request to one of the approvers the client "
                    "listed in their Control Panel: say who, and what needs agreeing (\"Adobe Acrobat licence, "
                    "Rs 18,000\"). The ticket moves to Waiting customer, so its SLA clock stops, and a note goes into "
                    "its thread. The approver answers in the portal; if they answer by phone or email instead, "
                    "<b>Record their answer</b> writes it down and says it was recorded by you. Either answer moves "
                    "the ticket back to In progress. A request nobody has answered for two days appears on the "
                    "needs-attention list.", body_s)]

story += [Paragraph("Technician hours", h2_s)]
story += [Paragraph("<b>Technician hours</b> shows each person's hours and output over any period — today, yesterday, "
                    "this or last month, this or last quarter. Client work and internal work are shown side by side "
                    "rather than blended: hours for clients, hours on the team's own boards, and tickets resolved on "
                    "each side. Export the same table by name for a spreadsheet or a review.", body_s)]
story += [Paragraph("Productivity scores are operational indicators only and must not be used as the sole basis for "
                    "employee performance decisions. The score only counts signals that are actually measured, and "
                    "reports how much of the model that covers.", note_s)]

story += [PageBreak()]

# ---- 4. Administration ----
story += [Paragraph("6. Administration", h1_s), hr()]

story += [Paragraph("PSA Connections", h2_s)]
story += [Paragraph("Connect Autotask and ConnectWise tenants under <b>PSA Connections</b>.", body_s)]
story += [bullets([
    "<b>Add connection</b> — name it, choose the provider, enter the API endpoint and credentials. "
    "Credentials are stored in a secure secret vault, never in the database and never shown again.",
    "<b>Edit</b> — change settings; leave the credential fields blank to keep the existing keys, or enter new ones to rotate.",
    "<b>Test</b> — runs a live check against the PSA and updates the connection's health status.",
    "<b>Boards</b> — discovers the connection's service boards/queues, statuses, priorities and categories live.",
])]

story += [Paragraph("Rotating PSA credentials", h2_s)]
story += [Paragraph("If an API key is ever exposed, rotate it promptly. The portal is built so this needs no "
                    "redeploy and no code or config changes:", body_s)]
story += [bullets([
    "In your PSA, revoke the exposed key and generate a new one. In <b>ConnectWise Manage</b> this is "
    "<b>System &rarr; Members &rarr; API Members</b>: open the member, delete the old key pair on the "
    "<b>API Keys</b> tab, then create a new pair. The new private key is shown only once — copy it immediately.",
    "In the portal, open <b>PSA Connections &rarr; Edit</b> for that connection and enter the new credentials. "
    "Sensitive fields are masked and are never pre-filled, so type the new key(s) and save.",
    "Leaving a credential field blank keeps the current value; only the fields you fill are overwritten in the "
    "secret vault.",
    "Click <b>Test</b> to confirm the new key works, then check <b>Integration Health</b>.",
])]
story += [Paragraph("Never paste API keys into chat, tickets, email, code, or config files. The portal only accepts "
                    "credentials through the masked fields on this screen, which write straight to the secret vault — "
                    "treat any key that has been shared anywhere else as compromised and rotate it.", note_s)]

story += [Paragraph("Field Mapping", h2_s)]
story += [Paragraph("Under <b>Field Mapping</b>, translate the portal's neutral values to each PSA's real values — this "
                    "is how the portal and your PSA agree on what a status, priority, queue or category means.", body_s)]
story += [bullets([
    "Pick a <b>connection</b> and a <b>field</b> (Status, Priority, Queue/Board, Category).",
    "For status and priority, map each portal value to a discovered PSA value.",
    "For queues and categories, add a mapping by naming a portal value and picking the PSA value.",
    "Each save is stored as a new <b>version</b> and recorded in the audit log, so changes can be reviewed.",
])]

story += [Paragraph("Scheduled reports", h2_s)]
story += [Paragraph("Under <b>Scheduled reports</b>, set the organization's time zone and create reports that send "
                    "themselves: technician productivity daily, weekly, monthly or quarterly, and a client business "
                    "review each quarter. Each runs at 07:00 in your time zone, covering the last complete period, and "
                    "arrives as a PDF with a spreadsheet beside it. Every run is kept in the portal to download even if "
                    "email is not set up.", body_s)]

story += [Paragraph("Email delivery", h2_s)]
story += [Paragraph("Reports need an account to send from. On <b>Integration Health</b>, use <b>Set up email</b>. "
                    "Microsoft 365 is the recommended route: create an app registration with the <b>Mail.Send</b> "
                    "permission and enter its tenant and client IDs and secret. Mail then leaves from a real mailbox, "
                    "which is what keeps it out of Junk. A plain mail server with a username and password also works. "
                    "<b>Send test email</b> proves it before anyone relies on it.", body_s)]

story += [Paragraph("Needs attention", h2_s)]
story += [Paragraph("It also lists tickets past their SLA, tickets that will breach within two hours, board tickets "
                    "still waiting for a first reply, and poor client ratings from the last week. Tickets waiting on "
                    "the customer or on hold are left out, and the item says how many. The Overview page shows the "
                    "same SLA picture as a banner that opens the Overdue and Due soon lists.", body_s)]
story += [Paragraph("Integration Health opens with one list of what is quietly going wrong: connections that failed or "
                    "stopped syncing, tickets that never reached the PSA, closed tickets with no closed date, and "
                    "reports nobody received. Add recipients and the same list is emailed each morning, but only on "
                    "days something is on it.", body_s)]

story += [Paragraph("Monitoring &amp; audit", h2_s)]
story += [bullets([
    "<b>Integration Health</b> — per-connection status, pending jobs, dead-lettered jobs and failed events.",
    "<b>Background Jobs</b> — monitor sync jobs and <b>reprocess</b> any that dead-lettered.",
    "<b>Audit Log</b> — an immutable record of administrative and security events (connection changes, mapping "
    "updates, tests, job reprocessing).",
])]

# ---- 5. Security ----
story += [Paragraph("7. Security &amp; your data", h1_s), hr()]
story += [bullets([
    "Each organization's data is fully isolated — you only ever see your own.",
    "PSA credentials live only in the secret vault; they are never returned to the browser or written to logs or the audit trail.",
    "Access is governed by role-based permissions (client user, client administrator, technician, manager, administrator, auditor).",
    "A technician sees the PSA tickets assigned to them, plus the unclaimed queue of any provider board they are granted. On the team's own boards they see every ticket, the same as the rest of the team, unless the board is limited to named members. Technicians can raise tickets on those boards, and a ticket they raise stays in their list. They can reply, log time and change status only on tickets they can see. On a board ticket, Edit details changes its title, priority, due date and the rest; resolving asks what fixed it; bringing a finished ticket back counts as a reopen; and History shows every step with who did it. Time can be dated up to 30 days back, and only its author or a board lead can change it. My work shows everything you hold across Autotask, ConnectWise, team boards and monitoring; Team workload shows leads who is carrying what and what nobody has picked up; on a board, Take it claims an unclaimed ticket from its row. Productivity now shows quality beside volume - reply promises kept, tickets that came back, client satisfaction - each with what it is based on, and hours split between client work, team boards and monitoring. Only time the PSA actually holds is counted. A board or a single topic can ask for review: resolved work then waits for a board lead, who approves it (it closes) or sends it back with a note (it returns to work, and that is not counted as a reopen); nobody reviews their own work, and Team workload shows what is awaiting review. Related tickets links one ticket to another - related, duplicate, part of, blocks - and only ever shows tickets you can see; a link changes no count. Productivity credits whoever does the work in the portal, whatever the PSA's assignee says: Take it on a ticket to say you are on it, and the person who resolves it gets the resolution, while hours go to whoever logged them. The PSA is not told and its assignment stays as it is. Only someone with their own PSA login needs linking to it; then work they do directly in the PSA counts as theirs too. Administrators can see the portal exactly as anyone else does: View as on a person's page, or View as... on the Users page to find any colleague or client. A yellow bar says whose view it is; nothing can be changed in it, every view is recorded, and Exit view brings you back. Workforce, when switched on, records when each person normally works and what they are skilled in: a weekly working window per day with planned breaks, night shifts that end the next morning, the person's time zone, and changes that start from a chosen day so past days keep their schedule. These hours are for planning work, not attendance. Skills come from your own catalogue, at a level each, and the Workforce page filters people by team and skill. Workforce also shows capacity: My capacity gives your working window for a day, the time you can use, what is already planned and your exact free windows; the Availability tab on a person shows their week. Someone who manages availability records time away (part of a day, or whole days) or extra availability outside the usual hours, and capacity changes at once; this is for planning work and is not a leave record. Team capacity lists each person's usable, planned and free time for a date, by team and skill. Find available technician answers who has one continuous free slot long enough for a piece of work, by date, time of day, team and skills, earliest first; it lists facts and books nothing. My plan shows what you are planning to work on for a day: your capacity, what is planned and what is free, each piece of work with who planned it, and Unscheduled work of mine - the open tickets you hold or that sit with your team and are not yet in your plan. Add to plan puts one into a free window; Internal work raises a small board ticket in your name and plans it in one step. Work you planned yourself you can move or take out; work a manager scheduled for you can be moved unless it is fixed, and only they can take it out or give it to someone else. Managers plan from a person's Plan tab or from a ticket's Planned work panel, can fix work in place, give it to someone else and, with a reason, override a clash. Planned time is a capacity boundary, not time logged. Team schedule shows managers everyone they may see for a day or a week on one board: when each person works, what is planned and what is free, with Unscheduled work beside it. Drag a piece of work onto a person, or press Plan, and say when; move, resize or give work away on the board, and the server checks each change before it is kept; a clash is refused or, with a reason, overridden. Work can be pencilled in as tentative: it shows in the plan but takes no capacity until someone confirms it, and confirming checks the time again. A ticket's Planned work panel records what the work needs - the effort, a window it must fall in, whether it may be split, a skill it asks for - and Plan with a window asks for that effort between two moments: the portal proposes where it fits, in one sitting or in pieces, says plainly what does not fit, and writes nothing until you confirm; if the plan changed meanwhile you see the new proposal first. The Planning queue lists the group's work that nobody has planned, why it waits (awaiting planning, nobody holds it, not enough capacity before the due date), what is due today, tomorrow or overdue, and what the group is short over the next fortnight. Planning never changes a due date. My day is where the work is done: what is planned today, what is running now and what is next; Start work puts a clock on a piece of work (one at a time; starting another asks whether to pause or stop the first), Pause and Resume keep paused time out of the count, and Stop turns the time into an ordinary time entry on the ticket, which goes to the PSA as any other and can be retried if the PSA rejects it. Add time records work done without a clock; a lead correcting someone else's time must say why. The day shows planned against actual for each piece of work (a fact, not a score), unplanned work started straight from the unscheduled list, and work outside normal hours as it was. Team today shows managers who is on what and how much of the plan is done. None of this is attendance: nothing records when anyone arrived or left, and nothing watches the computer. All of this is for staff only: clients never see schedules, capacity, plans or who is planned on their ticket.",
    "Attachments are validated and malware-scanned; downloads use short-lived, signed links.",
])]

# ---- 6. Help ----
story += [Paragraph("8. Getting help", h1_s), hr()]
story += [Paragraph("If a page shows a &ldquo;sign in&rdquo; or empty state where you expect data, your session may have "
                    "expired — sign in again. For anything else, contact your MSP administrator, who can review the "
                    "audit log and integration health to diagnose issues.", body_s)]

# ---- footer with page numbers ----
def footer(canvas, doc):
    canvas.saveState()
    canvas.setStrokeColor(LINE)
    canvas.setLineWidth(0.6)
    canvas.line(20*mm, 15*mm, 190*mm, 15*mm)
    canvas.setFont("Helvetica", 8)
    canvas.setFillColor(FAINT)
    canvas.drawString(20*mm, 10*mm, "Desk Portal — User Guide")
    canvas.drawRightString(190*mm, 10*mm, "Page %d" % doc.page)
    canvas.restoreState()

doc = SimpleDocTemplate(OUT, pagesize=A4,
                        leftMargin=20*mm, rightMargin=20*mm, topMargin=20*mm, bottomMargin=22*mm,
                        title="Desk Portal — User Guide", author="Desk Portal")
doc.build(story, onFirstPage=footer, onLaterPages=footer)
print("wrote", OUT)
