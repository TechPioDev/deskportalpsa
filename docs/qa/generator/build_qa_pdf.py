# -*- coding: utf-8 -*-
"""Builds the Desk Portal master test plan PDF.

Run:  python build_qa_pdf.py          (writes ../desk-portal-master-test-plan.pdf)
Needs: pip install reportlab

The cases live in qa_plan_part1..4.py as plain tuples, so editing the plan means editing
those lists - not this file, which is only layout.
"""
import os
from reportlab.lib import colors
from reportlab.lib.enums import TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import mm
from reportlab.platypus import (BaseDocTemplate, Frame, KeepTogether, PageBreak,
                                PageTemplate, Paragraph, Spacer, Table, TableStyle)

import qa_plan_part1  # noqa: F401  (registers modules 1-6)
import qa_plan_part2  # noqa: F401
import qa_plan_part3  # noqa: F401
import qa_plan_part4  # noqa: F401
from qa_plan_part1 import MODULES, E
from qa_plan_sql import LOG_LINES, SQL_COOKBOOK
from qa_results import RESULTS, RUN, status_of, tally

INK = colors.HexColor("#14532D")
ACCENT = colors.HexColor("#EA580C")
MUTED = colors.HexColor("#5B6B60")
RULE = colors.HexColor("#CBD8CE")
BG = colors.HexColor("#F4F7F4")

ss = getSampleStyleSheet()


def S(name, **kw):
    base = kw.pop("parent", ss["Normal"])
    return ParagraphStyle(name, parent=base, **kw)


TITLE = S("t", fontName="Helvetica-Bold", fontSize=26, leading=30, textColor=INK, spaceAfter=6)
SUB = S("s", fontName="Helvetica", fontSize=12, leading=16, textColor=MUTED, spaceAfter=4)
H1 = S("h1", fontName="Helvetica-Bold", fontSize=15, leading=19, textColor=INK,
       spaceBefore=16, spaceAfter=4)
H2 = S("h2", fontName="Helvetica-Bold", fontSize=11, leading=14, textColor=INK,
       spaceBefore=10, spaceAfter=3)
BODY = S("b", fontName="Helvetica", fontSize=9.2, leading=13, textColor=colors.HexColor("#1F2A22"),
         alignment=TA_LEFT, spaceAfter=3)
INTRO = S("i", fontName="Helvetica-Oblique", fontSize=9.4, leading=13.5, textColor=MUTED,
          spaceAfter=6)
CASEID = S("cid", fontName="Helvetica-Bold", fontSize=9.6, leading=13, textColor=ACCENT)
CASET = S("ct", fontName="Helvetica-Bold", fontSize=9.6, leading=13, textColor=INK)
LBL = S("l", fontName="Helvetica-Bold", fontSize=8.6, leading=12, textColor=MUTED)
VAL = S("v", fontName="Helvetica", fontSize=9, leading=12.6,
        textColor=colors.HexColor("#1F2A22"))

DOC_TITLE = "Desk Portal / PIO Manage - Master Test Plan"


def header_footer(canvas, doc):
    canvas.saveState()
    canvas.setFont("Helvetica", 7.5)
    canvas.setFillColor(MUTED)
    canvas.drawString(18 * mm, 287 * mm, DOC_TITLE)
    canvas.drawRightString(192 * mm, 287 * mm, "piomanage.com")
    canvas.setStrokeColor(RULE)
    canvas.setLineWidth(0.4)
    canvas.line(18 * mm, 285 * mm, 192 * mm, 285 * mm)
    canvas.line(18 * mm, 14 * mm, 192 * mm, 14 * mm)
    canvas.drawString(18 * mm, 9 * mm, "Generated for QA sign-off")
    canvas.drawRightString(192 * mm, 9 * mm, "Page %d" % doc.page)
    canvas.restoreState()


RESULT_COLOR = {
    "Pass": colors.HexColor("#1B7F3B"),
    "Partial": colors.HexColor("#B26A00"),
    "Blocked": colors.HexColor("#8A6D3B"),
    "N/A": MUTED,
    "Fail": colors.HexColor("#B3261E"),
}


def result_cell(cid):
    """The recorded outcome, or a blank line to write one on."""
    status, evidence = status_of(cid)
    if status == "Not run":
        return Paragraph("Pass / Fail / Blocked ................  Tester ................  "
                         "Date ................", VAL)
    colour = RESULT_COLOR.get(status, MUTED)
    return Paragraph(
        '<font color="#%s"><b>%s</b></font>  %s' % (colour.hexval()[2:], E(status), E(evidence)),
        VAL)


def case_block(cid, title, steps, expected, verify):
    rows = [
        [Paragraph(E(cid), CASEID), Paragraph(E(title), CASET)],
        [Paragraph("How to test", LBL), Paragraph(E(steps), VAL)],
        [Paragraph("Expected", LBL), Paragraph(E(expected), VAL)],
        [Paragraph("Verify with", LBL), Paragraph(E(verify), VAL)],
        [Paragraph("Result", LBL), result_cell(cid)],
    ]
    t = Table(rows, colWidths=[26 * mm, 148 * mm])
    t.setStyle(TableStyle([
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("TOPPADDING", (0, 0), (-1, -1), 2.2),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 2.2),
        ("LEFTPADDING", (0, 0), (-1, -1), 4),
        ("RIGHTPADDING", (0, 0), (-1, -1), 4),
        ("BACKGROUND", (0, 0), (-1, 0), BG),
        ("LINEBELOW", (0, 0), (-1, 0), 0.5, RULE),
        ("BOX", (0, 0), (-1, -1), 0.5, RULE),
        ("TEXTCOLOR", (0, 4), (0, 4), MUTED),
    ]))
    return KeepTogether([t, Spacer(1, 6)])


story = []

# ---------- cover ----------
story.append(Spacer(1, 28 * mm))
story.append(Paragraph("Master Test Plan", TITLE))
story.append(Paragraph("Desk Portal / PIO Manage - multi-tenant PSA ticket portal", SUB))
story.append(Spacer(1, 8))
story.append(Paragraph(
    "A complete, executable test pass over the shipped build: authentication, tenant isolation, "
    "roles and permissions, employee and technician registration, PSA connections, field mapping, "
    "the ticket sync engine, ticket lifecycle and status, notes, time tracking, attachments, "
    "productivity analytics, the client control panel, the AI assistant, and operations.", BODY))
story.append(Spacer(1, 10))

meta = Table([
    ["Scope", "Autotask + ConnectWise Manage connectors, staff dashboard, client portal, "
              "control panel, public site"],
    ["Environment", "https://piomanage.com (production) - use a dedicated test client company"],
    ["Roles needed", "Platform admin, MSP admin, Manager, Technician, Auditor, Client admin, "
                     "Client user"],
    ["Test cases", "%d across %d modules" % (sum(len(c) for _, _, c in MODULES), len(MODULES))],
    ["Prepared", "September 2026"],
], colWidths=[30 * mm, 144 * mm])
meta.setStyle(TableStyle([
    ("VALIGN", (0, 0), (-1, -1), "TOP"),
    ("FONTNAME", (0, 0), (0, -1), "Helvetica-Bold"),
    ("FONTNAME", (1, 0), (1, -1), "Helvetica"),
    ("FONTSIZE", (0, 0), (-1, -1), 9),
    ("TEXTCOLOR", (0, 0), (0, -1), MUTED),
    ("TOPPADDING", (0, 0), (-1, -1), 3),
    ("BOTTOMPADDING", (0, 0), (-1, -1), 3),
    ("LINEBELOW", (0, 0), (-1, -2), 0.4, RULE),
]))
story.append(meta)

story.append(Spacer(1, 14))
story.append(Paragraph("How to use this document", H2))
story.append(Paragraph(
    "Every case is self-contained: what to do, what should happen, and where to look to prove it. "
    "Where a check can be faked by a passing screen, the case names the database query or log line "
    "that settles it instead. Work top to bottom the first time - later modules assume tickets and "
    "users created by earlier ones.", BODY))
story.append(Spacer(1, 4))
story.append(Paragraph(
    "Three rules that decide whether a pass means anything in this system:", BODY))
for line in [
    "1. Confirm the build under test is newer than the fix you are testing. More wasted QA time "
    "here has come from stale builds than from real defects.",
    "2. A check that matches something already true is not a check. When waiting for a sync or a "
    "rollup, capture the current timestamp first and wait for a DIFFERENT one.",
    "3. Absence of an error is not evidence. An unmapped value, a skipped ticket and a truncated "
    "import all look exactly like normal, successful operation.",
]:
    story.append(Paragraph(E(line), BODY))

story.append(PageBreak())

# ---------- environment ----------
story.append(Paragraph("Test environment and data setup", H1))
story.append(Paragraph(
    "Do this once before starting. Several modules need more data than a quiet sandbox contains.",
    INTRO))

setup = [
    ("Accounts", "Create one user per role: MSP admin, Manager, Senior Technician, Standard "
                 "Technician, Auditor, plus a client administrator and a plain client user on a "
                 "test company. Keep the passwords in your own password manager - never in a "
                 "ticket or chat."),
    ("PSA sandbox", "Use a non-production PSA tenant where you may freely change ticket statuses "
                    "and queues. Several cases require moving tickets between states."),
    ("Volume", "Ensure the PSA holds MORE tickets in range than the page size of 100. Pagination "
               "defects are invisible below that threshold."),
    ("Variety", "Ensure there is at least one ticket in each of: open, resolved, closed, an "
                "unmapped status, and a second queue or board. On ConnectWise, use two boards."),
    ("Database access", "docker exec desk-portal-prod-postgres-1 psql -U desk -d desk_portal - "
                        "tables are snake_case, columns are quoted PascalCase."),
    ("Force a full re-sync", "update psa_connections set \"LastSuccessfulSyncAt\"=NULL; then wait "
                             "for the timestamp to return. This is the single most useful command "
                             "in this document."),
    ("Worker log", "docker logs desk-portal-prod-worker-1 --since 30m - Serilog JSON, so pipe "
                   "through python -m json.tool or grep on the field names."),
    ("Unit suite", "dotnet test tests/unit - 535 tests. Run before and after any code change."),
]
rows = [[Paragraph("<b>%s</b>" % E(k), VAL), Paragraph(E(v), VAL)] for k, v in setup]
t = Table(rows, colWidths=[32 * mm, 142 * mm])
t.setStyle(TableStyle([
    ("VALIGN", (0, 0), (-1, -1), "TOP"),
    ("TOPPADDING", (0, 0), (-1, -1), 3),
    ("BOTTOMPADDING", (0, 0), (-1, -1), 3),
    ("LEFTPADDING", (0, 0), (-1, -1), 4),
    ("BOX", (0, 0), (-1, -1), 0.5, RULE),
    ("INNERGRID", (0, 0), (-1, -1), 0.4, RULE),
    ("BACKGROUND", (0, 0), (0, -1), BG),
]))
story.append(t)
story.append(PageBreak())

# ---------- run results ----------
story.append(Paragraph("Results of the recorded run", H1))
story.append(Paragraph(
    "Outcomes are stamped on each case below. This page is the summary; the detail is in the "
    "Result row of every case.", INTRO))

runrows = [[Paragraph("<b>%s</b>" % E(k.title()), VAL), Paragraph(E(v), VAL)]
           for k, v in RUN.items()]
t = Table(runrows, colWidths=[30 * mm, 144 * mm])
t.setStyle(TableStyle([
    ("VALIGN", (0, 0), (-1, -1), "TOP"),
    ("TOPPADDING", (0, 0), (-1, -1), 3),
    ("BOTTOMPADDING", (0, 0), (-1, -1), 3),
    ("LEFTPADDING", (0, 0), (-1, -1), 4),
    ("BOX", (0, 0), (-1, -1), 0.5, RULE),
    ("INNERGRID", (0, 0), (-1, -1), 0.4, RULE),
    ("BACKGROUND", (0, 0), (0, -1), BG),
]))
story.append(t)
story.append(Spacer(1, 10))

counts = tally()
total_cases = sum(len(c) for _, _, c in MODULES)
recorded = sum(counts.values())
head = ["Pass", "Partial", "Blocked", "N/A", "Fail", "Not run"]
row = [str(counts.get(h, 0)) for h in head[:-1]] + [str(total_cases - recorded)]
t = Table([head, row], colWidths=[29 * mm] * 6)
t.setStyle(TableStyle([
    ("FONTNAME", (0, 0), (-1, 0), "Helvetica-Bold"),
    ("FONTNAME", (0, 1), (-1, 1), "Helvetica-Bold"),
    ("FONTSIZE", (0, 0), (-1, -1), 10),
    ("ALIGN", (0, 0), (-1, -1), "CENTER"),
    ("BACKGROUND", (0, 0), (-1, 0), BG),
    ("TEXTCOLOR", (0, 1), (0, 1), colors.HexColor("#1B7F3B")),
    # Fail is the fifth column. Leaving it the same ink as the rest is how a one in that box gets
    # read as just another number on the way to the pass count.
    ("TEXTCOLOR", (4, 1), (4, 1), colors.HexColor("#B3261E")),
    ("BOX", (0, 0), (-1, -1), 0.5, RULE),
    ("INNERGRID", (0, 0), (-1, -1), 0.4, RULE),
    ("TOPPADDING", (0, 0), (-1, -1), 5),
    ("BOTTOMPADDING", (0, 0), (-1, -1), 5),
]))
story.append(t)
story.append(Spacer(1, 8))
story.append(Paragraph(
    "One failure, and it is the one to read first. PUB-07 found twelve unfilled placeholders live "
    "on the privacy policy and terms - retention periods, the liability cap and governing law all "
    "still reading \"e.g. 24 months\" or \"your jurisdiction\" to anyone who visits. It survived an "
    "earlier scan because the slots hold ordinary English rather than TBD or TODO, which is worth "
    "remembering: a check that looks for placeholder MARKERS cannot find a placeholder written as "
    "prose. It needs the owner's real values and no amount of testing will supply them.", BODY))
story.append(Spacer(1, 6))
story.append(Paragraph(
    "Everything else that could be executed passed. Blocked and N/A are not quiet passes. Blocked "
    "needs something this run did not have - a login, a write into the PSA, a second tenant - and "
    "two blocked cases are blocked because running them would disturb production rather than "
    "because they were out of reach. N/A means the case cannot arise here at all, which is itself "
    "a finding: the cross-tenant cases have no second tenant to cross into, and the retention case "
    "has nothing old enough to expire.", BODY))
story.append(Spacer(1, 6))
story.append(Paragraph(
    "Several defects were found and fixed while running this - pagination stopping at 100 tickets, "
    "two fields missing from the update hash, mapping that had never worked on either provider, "
    "Autotask client companies named after their id, a secret-scanning gate that had silently "
    "stopped scanning, and a public contact form that accepted a 60,000-character message, stored "
    "the first 4,000 and thanked the sender. Those are recorded in the modules they belong to "
    "rather than here.", BODY))
story.append(PageBreak())

# ---------- modules ----------
for name, intro, cases in MODULES:
    story.append(Paragraph(E(name), H1))
    story.append(Paragraph(E(intro), INTRO))
    for c in cases:
        story.append(case_block(*c))

# ---------- appendix ----------
story.append(PageBreak())
story.append(Paragraph("Appendix A - SQL cookbook", H1))
story.append(Paragraph(
    "Paste-ready checks. Run inside: docker exec desk-portal-prod-postgres-1 psql -U desk "
    "-d desk_portal -c \"...\"", INTRO))

sql = SQL_COOKBOOK
for label, q in sql:
    story.append(Paragraph("<b>%s</b>" % E(label), VAL))
    story.append(Paragraph('<font face="Courier" size="7.8">%s</font>' % E(q), BODY))
    story.append(Spacer(1, 3))

story.append(Spacer(1, 8))
story.append(Paragraph("Appendix B - Log lines worth grepping", H1))
logs = LOG_LINES
rows = [[Paragraph('<font face="Courier" size="8">%s</font>' % E(k), VAL),
         Paragraph(E(v), VAL)] for k, v in logs]
t = Table(rows, colWidths=[52 * mm, 122 * mm])
t.setStyle(TableStyle([
    ("VALIGN", (0, 0), (-1, -1), "TOP"),
    ("TOPPADDING", (0, 0), (-1, -1), 3),
    ("BOTTOMPADDING", (0, 0), (-1, -1), 3),
    ("LEFTPADDING", (0, 0), (-1, -1), 4),
    ("BOX", (0, 0), (-1, -1), 0.5, RULE),
    ("INNERGRID", (0, 0), (-1, -1), 0.4, RULE),
]))
story.append(t)

story.append(Spacer(1, 10))
story.append(Paragraph("Appendix C - Sign-off", H1))
signoff = [["Module", "Cases", "Pass", "Fail", "Blocked", "Tester", "Date"]]
for name, _, cases in MODULES:
    signoff.append([name.split(". ", 1)[-1][:34], str(len(cases)), "", "", "", "", ""])
t = Table(signoff, colWidths=[54 * mm, 14 * mm, 14 * mm, 14 * mm, 18 * mm, 30 * mm, 30 * mm],
          repeatRows=1)
t.setStyle(TableStyle([
    ("FONTNAME", (0, 0), (-1, 0), "Helvetica-Bold"),
    ("FONTSIZE", (0, 0), (-1, -1), 8),
    ("BACKGROUND", (0, 0), (-1, 0), BG),
    ("TEXTCOLOR", (0, 0), (-1, 0), INK),
    ("BOX", (0, 0), (-1, -1), 0.5, RULE),
    ("INNERGRID", (0, 0), (-1, -1), 0.4, RULE),
    ("TOPPADDING", (0, 0), (-1, -1), 3.5),
    ("BOTTOMPADDING", (0, 0), (-1, -1), 3.5),
]))
story.append(t)

story.append(Spacer(1, 10))
story.append(Paragraph("Known gaps this plan does not cover", H2))
story.append(Paragraph(
    "Stated plainly so nobody assumes otherwise. There is no SMTP anywhere in the stack, so no "
    "email delivery can be tested - enquiries and reports are stored for in-portal download. "
    "Load testing (k6), DAST and a penetration test are authored but have never been run against "
    "the live stack. Disaster recovery has a documented procedure but no rehearsed restore. "
    "ConnectWise closure dates cannot be verified until a ticket is actually closed in that "
    "sandbox, and the legal pages still contain unfilled placeholders.", BODY))

# Defaults to docs/qa, one level up from this script, so a bare `python build_qa_pdf.py`
# regenerates the committed PDF in place. Override with QA_OUT to write elsewhere.
out_dir = os.environ.get("QA_OUT") or os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
out = os.path.join(out_dir, "desk-portal-master-test-plan.pdf")
# invariant=1 fixes the embedded creation date and document id. Without it every rebuild
# produces a byte-different file, so the committed PDF would show as modified even when the
# plan had not changed - and a diff that always appears is a diff nobody reads.
doc = BaseDocTemplate(out, pagesize=A4,
                      leftMargin=18 * mm, rightMargin=18 * mm,
                      topMargin=20 * mm, bottomMargin=18 * mm,
                      title=DOC_TITLE, author="QA", invariant=1)
frame = Frame(doc.leftMargin, doc.bottomMargin, doc.width, doc.height, id="f")
doc.addPageTemplates([PageTemplate(id="all", frames=[frame], onPage=header_footer)])
doc.build(story)

total = sum(len(c) for _, _, c in MODULES)
print("modules:", len(MODULES), "cases:", total)
print("written:", out)
