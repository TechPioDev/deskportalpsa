"""Render the Desk Portal project document (a small markdown subset) to a print-ready PDF.

Supported source syntax, one block per blank-line-separated chunk:
  # / ## / ### headings, - bullets, 1. numbered, | tables |, > callout, plain paragraphs,
  ---PAGEBREAK---, **bold** and `code` inline.
"""
import re
import sys
from reportlab.lib import colors
from reportlab.lib.enums import TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import mm
from reportlab.platypus import (BaseDocTemplate, Frame, KeepTogether, ListFlowable, ListItem,
                                PageBreak, PageTemplate, Paragraph, Spacer, Table, TableStyle)

INK = colors.HexColor('#12232E')
MUTED = colors.HexColor('#5A6B77')
BRAND = colors.HexColor('#1F6F54')
RULE = colors.HexColor('#D8DEE3')
TINT = colors.HexColor('#EEF3F1')

styles = getSampleStyleSheet()


def S(name, **kw):
    base = kw.pop('parent', styles['BodyText'])
    return ParagraphStyle(name, parent=base, **kw)


BODY = S('body', fontName='Helvetica', fontSize=9.6, leading=14.2, textColor=INK,
         spaceAfter=6, alignment=TA_LEFT)
H1 = S('h1', fontName='Helvetica-Bold', fontSize=18, leading=22, textColor=INK,
       spaceBefore=4, spaceAfter=10)
H2 = S('h2', fontName='Helvetica-Bold', fontSize=13, leading=17, textColor=BRAND,
       spaceBefore=14, spaceAfter=6)
H3 = S('h3', fontName='Helvetica-Bold', fontSize=10.6, leading=14, textColor=INK,
       spaceBefore=10, spaceAfter=4)
BULLET = S('bullet', parent=BODY, spaceAfter=3, leading=13.4)
CALLOUT = S('callout', parent=BODY, fontSize=9.4, leading=13.6, textColor=INK,
            leftIndent=8, rightIndent=8, spaceBefore=4, spaceAfter=4)
CELL = S('cell', parent=BODY, fontSize=8.6, leading=11.6, spaceAfter=0)
CELL_H = S('cellh', parent=CELL, fontName='Helvetica-Bold', textColor=colors.white)
COVER_TITLE = S('ct', fontName='Helvetica-Bold', fontSize=30, leading=35, textColor=INK, spaceAfter=6)
COVER_SUB = S('cs', fontName='Helvetica', fontSize=13, leading=18, textColor=MUTED, spaceAfter=18)
COVER_META = S('cm', fontName='Helvetica', fontSize=10, leading=16, textColor=INK)


def inline(text):
    text = (text.replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;'))
    text = re.sub(r'\*\*(.+?)\*\*', r'<b>\1</b>', text)
    text = re.sub(r'`(.+?)`', r'<font face="Courier" size="8.6" color="#1F6F54">\1</font>', text)
    return text


def table(rows):
    header, body = rows[0], rows[1:]
    data = [[Paragraph(inline(c), CELL_H) for c in header]]
    data += [[Paragraph(inline(c), CELL) for c in r] for r in body]
    widths = None
    n = len(header)
    avail = 170 * mm
    if n == 2:
        widths = [avail * 0.34, avail * 0.66]
    elif n == 3:
        widths = [avail * 0.26, avail * 0.20, avail * 0.54]
    elif n == 4:
        widths = [avail * 0.22, avail * 0.16, avail * 0.16, avail * 0.46]
    t = Table(data, colWidths=widths, repeatRows=1, hAlign='LEFT')
    t.setStyle(TableStyle([
        ('BACKGROUND', (0, 0), (-1, 0), BRAND),
        ('ROWBACKGROUNDS', (0, 1), (-1, -1), [colors.white, TINT]),
        ('GRID', (0, 0), (-1, -1), 0.4, RULE),
        ('VALIGN', (0, 0), (-1, -1), 'TOP'),
        ('LEFTPADDING', (0, 0), (-1, -1), 5),
        ('RIGHTPADDING', (0, 0), (-1, -1), 5),
        ('TOPPADDING', (0, 0), (-1, -1), 4),
        ('BOTTOMPADDING', (0, 0), (-1, -1), 4),
    ]))
    return t


def callout(lines):
    t = Table([[Paragraph(inline(' '.join(lines)), CALLOUT)]], colWidths=[170 * mm], hAlign='LEFT')
    t.setStyle(TableStyle([
        ('BACKGROUND', (0, 0), (-1, -1), TINT),
        ('LINEBEFORE', (0, 0), (0, -1), 2.2, BRAND),
        ('LEFTPADDING', (0, 0), (-1, -1), 8),
        ('RIGHTPADDING', (0, 0), (-1, -1), 8),
        ('TOPPADDING', (0, 0), (-1, -1), 6),
        ('BOTTOMPADDING', (0, 0), (-1, -1), 6),
    ]))
    return t


def build(src_path, out_path, title, subtitle, meta_lines):
    raw_lines = open(src_path, encoding='utf-8').read().split('\n')
    story = [Spacer(1, 38 * mm), Paragraph(title, COVER_TITLE), Paragraph(subtitle, COVER_SUB)]
    for line in meta_lines:
        story.append(Paragraph(inline(line), COVER_META))
    story.append(PageBreak())

    pending_bullets, pending_numbers = [], []

    def flush():
        nonlocal pending_bullets, pending_numbers
        if pending_bullets:
            story.append(ListFlowable(
                [ListItem(Paragraph(inline(b), BULLET), leftIndent=12) for b in pending_bullets],
                bulletType='bullet', bulletColor=BRAND, bulletFontSize=6, start='square',
                leftIndent=12, spaceAfter=6))
            pending_bullets = []
        if pending_numbers:
            story.append(ListFlowable(
                [ListItem(Paragraph(inline(b), BULLET), leftIndent=14) for b in pending_numbers],
                bulletType='1', bulletColor=INK, leftIndent=14, spaceAfter=6))
            pending_numbers = []

    para_buf, table_buf, quote_buf = [], [], []

    def flush_para():
        if para_buf:
            story.append(Paragraph(inline(' '.join(para_buf)), BODY))
            para_buf.clear()

    def flush_table():
        if table_buf:
            rows = [[c.strip() for c in l.strip().strip('|').split('|')] for l in table_buf
                    if not re.match(r'^\|[\s:|-]+\|$', l.strip())]
            story.append(table(rows))
            story.append(Spacer(1, 5))
            table_buf.clear()

    def flush_quote():
        if quote_buf:
            story.append(callout(quote_buf))
            quote_buf.clear()

    def flush_all():
        flush_para(); flush(); flush_table(); flush_quote()

    for raw in raw_lines:
        line = raw.rstrip()
        stripped = line.strip()

        if stripped.startswith('|'):
            flush_para(); flush(); flush_quote()
            table_buf.append(stripped)
            continue
        flush_table()

        if stripped.startswith('> '):
            flush_para(); flush()
            quote_buf.append(stripped[2:])
            continue
        flush_quote()

        if not stripped:
            flush_para(); flush()
            continue
        if stripped == '---PAGEBREAK---':
            flush_all(); story.append(PageBreak()); continue
        if stripped.startswith('### '):
            flush_all(); story.append(Paragraph(inline(stripped[4:]), H3)); continue
        if stripped.startswith('## '):
            flush_all(); story.append(Paragraph(inline(stripped[3:]), H2)); continue
        if stripped.startswith('# '):
            flush_all(); story.append(Paragraph(inline(stripped[2:]), H1)); continue
        if stripped.startswith('- '):
            flush_para()
            if pending_numbers: flush()
            pending_bullets.append(stripped[2:]); continue
        if re.match(r'^\d+\. ', stripped):
            flush_para()
            if pending_bullets: flush()
            pending_numbers.append(re.sub(r'^\d+\.\s*', '', stripped)); continue
        flush()
        para_buf.append(stripped)
    flush_all()

    def decorate(canvas, doc):
        canvas.saveState()
        if doc.page == 1:
            canvas.setFillColor(BRAND)
            canvas.rect(0, A4[1] - 14 * mm, A4[0], 14 * mm, stroke=0, fill=1)
            canvas.setFillColor(TINT)
            canvas.rect(0, 0, A4[0], 10 * mm, stroke=0, fill=1)
        else:
            canvas.setStrokeColor(RULE)
            canvas.setLineWidth(0.4)
            canvas.line(20 * mm, A4[1] - 15 * mm, A4[0] - 20 * mm, A4[1] - 15 * mm)
            canvas.setFont('Helvetica', 7.6)
            canvas.setFillColor(MUTED)
            canvas.drawString(20 * mm, A4[1] - 12.5 * mm, title)
            canvas.drawRightString(A4[0] - 20 * mm, A4[1] - 12.5 * mm, subtitle)
            canvas.line(20 * mm, 14 * mm, A4[0] - 20 * mm, 14 * mm)
            canvas.drawString(20 * mm, 10 * mm, meta_lines[0].replace('**', ''))
            canvas.drawRightString(A4[0] - 20 * mm, 10 * mm, 'Page %d' % (doc.page - 1))
        canvas.restoreState()

    doc = BaseDocTemplate(out_path, pagesize=A4, leftMargin=20 * mm, rightMargin=20 * mm,
                          topMargin=22 * mm, bottomMargin=20 * mm, title=title, author='Desk Portal')
    frame = Frame(doc.leftMargin, doc.bottomMargin, doc.width, doc.height, id='f')
    doc.addPageTemplates([PageTemplate(id='main', frames=[frame], onPage=decorate)])
    doc.build(story)
    print('wrote', out_path)


if __name__ == '__main__':
    build(sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5:])
