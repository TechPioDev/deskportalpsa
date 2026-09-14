using System.Globalization;
using System.Text;
using Desk.Application.Reporting;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using R = Desk.Infrastructure.Reporting.TechnicianReportRenderer;

namespace Desk.Infrastructure.Reporting;

/// <summary>CSV and PDF renderings of a <see cref="ClientQbr"/>, sharing the technician report's type and table styles.</summary>
public static class ClientQbrRenderer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string ToCsv(ClientQbr q)
    {
        var sb = new StringBuilder();
        void Line(params object?[] cells) => sb.Append(string.Join(',', cells.Select(R.Cell))).Append("\r\n");

        Line(q.Title);
        Line("Organization", q.OrganizationName);
        Line("Client", q.ClientName);
        Line("Period", q.PeriodStart.ToString("yyyy-MM-dd", Inv), q.PeriodEnd.ToString("yyyy-MM-dd", Inv), q.TimeZone);
        Line();
        Line("Measure", q.PeriodLabel, q.PreviousLabel);
        Line("Tickets raised", q.Current.Raised, q.Previous.Raised);
        Line("Tickets resolved", q.Current.Resolved, q.Previous.Resolved);
        Line("Open at period end", q.Current.OpenAtEnd, q.Previous.OpenAtEnd);
        Line("Hours worked", q.Current.Hours, q.Previous.Hours);
        Line("Billable hours", q.Current.BillableHours, q.Previous.BillableHours);
        Line("Resolved within SLA %", q.Current.SlaPct, q.Previous.SlaPct);
        Line("SLA sample (resolved with a target)", q.Current.SlaEligible, q.Previous.SlaEligible);
        Line("Average resolution hours", q.Current.AvgResolutionHours, q.Previous.AvgResolutionHours);
        Line("Median resolution hours", q.Current.MedianResolutionHours, q.Previous.MedianResolutionHours);

        if (q.Months.Count > 0)
        {
            Line();
            Line("Month", "Raised", "Resolved", "Hours");
            foreach (var m in q.Months) Line(m.Month.ToString("yyyy-MM", Inv), m.Raised, m.Resolved, m.Hours);
        }
        Line();
        Line("Priority", "Tickets raised");
        foreach (var c in q.ByPriority) Line(c.Label, c.Count);
        Line();
        Line("Category", "Tickets raised");
        foreach (var c in q.ByCategory) Line(c.Label, c.Count);
        Line();
        Line("Technician", "Hours", "Billable hours", "Resolved", "Tickets touched");
        foreach (var t in q.Technicians) Line(t.Name, t.Hours, t.BillableHours, t.Resolved, t.TicketsTouched);
        Line();
        Line("Oldest open tickets", "Title", "Priority", "Status", "Age (days)");
        foreach (var o in q.OldestOpen) Line(o.Reference, o.Title, o.Priority, o.Status, o.AgeDays);
        return sb.ToString();
    }

    public static byte[] ToPdf(ClientQbr q)
    {
        EmbeddedFonts.EnsureRegistered();

        var doc = new Document { Info = { Title = q.Title, Author = q.OrganizationName } };
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = R.Font;
        normal.Font.Size = 9.5;
        normal.Font.Color = R.Ink;

        var section = doc.AddSection();
        section.PageSetup = doc.DefaultPageSetup.Clone();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(1.8);
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.6);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.8);

        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 8;
        footer.Format.Font.Color = R.Muted;
        footer.AddText($"{q.OrganizationName} for {q.ClientName} · generated {q.GeneratedAt.UtcDateTime.ToString("d MMM yyyy", Inv)} · page ");
        footer.AddPageField();
        footer.AddText(" of ");
        footer.AddNumPagesField();

        var eyebrow = section.AddParagraph($"{q.OrganizationName.ToUpperInvariant()} · BUSINESS REVIEW");
        eyebrow.Format.Font.Size = 8;
        eyebrow.Format.Font.Color = R.Accent;
        eyebrow.Format.Font.Bold = true;

        var title = section.AddParagraph(q.ClientName);
        title.Format.Font.Size = 22;
        title.Format.Font.Bold = true;
        title.Format.SpaceBefore = 2;

        var sub = section.AddParagraph($"{q.PeriodLabel} · {q.PeriodStart.ToString("d MMM yyyy", Inv)} – {q.PeriodEnd.ToString("d MMM yyyy", Inv)} · compared with {q.PreviousLabel}");
        sub.Format.Font.Color = R.Muted;
        sub.Format.SpaceAfter = 14;

        // Headline tiles with the change against the previous period under each.
        var tiles = section.AddTable();
        tiles.Borders.Visible = false;
        for (var i = 0; i < 4; i++) tiles.AddColumn(Unit.FromCentimeter(4.35));
        var labels = tiles.AddRow();
        var values = tiles.AddRow();
        (string Label, string Value, string Note)[] figures =
        [
            ("Tickets raised", q.Current.Raised.ToString(Inv), Change(q.Current.Raised, q.Previous.Raised)),
            ("Tickets resolved", q.Current.Resolved.ToString(Inv), Change(q.Current.Resolved, q.Previous.Resolved)),
            ("Hours worked", R.Hrs(q.Current.Hours) + "h", Change(q.Current.Hours, q.Previous.Hours)),
            ("Resolved within SLA", q.Current.SlaPct is { } s ? s.ToString("0.#", Inv) + "%" : "—",
                q.Current.SlaPct is null ? "no SLA targets set" : $"{q.Current.WithinSla} of {q.Current.SlaEligible} · was {(q.Previous.SlaPct is { } p ? p.ToString("0.#", Inv) + "%" : "—")}"),
        ];
        for (var i = 0; i < 4; i++)
        {
            var l = labels.Cells[i].AddParagraph(figures[i].Label);
            l.Format.Font.Size = 8;
            l.Format.Font.Color = R.Muted;
            var v = values.Cells[i].AddParagraph(figures[i].Value);
            v.Format.Font.Size = 17;
            v.Format.Font.Bold = true;
            var n = values.Cells[i].AddParagraph(figures[i].Note);
            n.Format.Font.Size = 8;
            n.Format.Font.Color = R.Muted;
        }

        R.Heading(section, "Service at a glance");
        var glance = R.DataTable(section, [("Measure", 7.4, false), (q.PeriodLabel, 3.2, true), (q.PreviousLabel, 3.2, true)]);
        var odd = false;
        void G(string m, string a, string b) => R.DataRow(glance, odd = !odd, false, m, a, b);
        G("Tickets raised", q.Current.Raised.ToString(Inv), q.Previous.Raised.ToString(Inv));
        G("Tickets resolved", q.Current.Resolved.ToString(Inv), q.Previous.Resolved.ToString(Inv));
        G("Open at the end of the period", q.Current.OpenAtEnd.ToString(Inv), q.Previous.OpenAtEnd.ToString(Inv));
        G("Hours worked (billable)", $"{R.Hrs(q.Current.Hours)} ({R.Hrs(q.Current.BillableHours)})", $"{R.Hrs(q.Previous.Hours)} ({R.Hrs(q.Previous.BillableHours)})");
        G("Resolved within SLA", Pct(q.Current), Pct(q.Previous));
        G("Average time to resolve", Dur(q.Current.AvgResolutionHours), Dur(q.Previous.AvgResolutionHours));
        G("Median time to resolve", Dur(q.Current.MedianResolutionHours), Dur(q.Previous.MedianResolutionHours));

        if (q.Months.Count > 0)
        {
            R.Heading(section, "Month by month");
            var t = R.DataTable(section, [("Month", 5.0, false), ("Raised", 2.6, true), ("Resolved", 2.6, true), ("Hours", 2.6, true)]);
            odd = false;
            foreach (var m in q.Months)
                R.DataRow(t, odd = !odd, false, m.Month.ToString("MMMM yyyy", Inv), m.Raised.ToString(Inv), m.Resolved.ToString(Inv), R.Hrs(m.Hours));
        }

        if (q.ByCategory.Count > 0 || q.ByPriority.Count > 0)
        {
            R.Heading(section, "What the requests were about");
            var t = section.AddTable();
            t.Borders.Visible = false;
            t.AddColumn(Unit.FromCentimeter(5.4)); t.AddColumn(Unit.FromCentimeter(1.6)); t.AddColumn(Unit.FromCentimeter(1.0));
            t.AddColumn(Unit.FromCentimeter(5.4)); t.AddColumn(Unit.FromCentimeter(1.6));
            t.Columns[1].Format.Alignment = ParagraphAlignment.Right;
            t.Columns[4].Format.Alignment = ParagraphAlignment.Right;
            var head = t.AddRow();
            head.Format.Font.Size = 8;
            head.Format.Font.Color = R.Muted;
            head.Cells[0].AddParagraph("Category");
            head.Cells[1].AddParagraph("Tickets");
            head.Cells[3].AddParagraph("Priority");
            head.Cells[4].AddParagraph("Tickets");
            for (var i = 0; i < Math.Max(q.ByCategory.Count, q.ByPriority.Count); i++)
            {
                var row = t.AddRow();
                row.TopPadding = row.BottomPadding = 2;
                if (i < q.ByCategory.Count) { row.Cells[0].AddParagraph(q.ByCategory[i].Label); row.Cells[1].AddParagraph(q.ByCategory[i].Count.ToString(Inv)); }
                if (i < q.ByPriority.Count) { row.Cells[3].AddParagraph(q.ByPriority[i].Label); row.Cells[4].AddParagraph(q.ByPriority[i].Count.ToString(Inv)); }
            }
        }

        if (q.Technicians.Count > 0)
        {
            R.Heading(section, "Who did the work");
            var t = R.DataTable(section, [("Technician", 6.4, false), ("Hours", 2.4, true), ("Billable", 2.4, true), ("Resolved", 2.4, true), ("Tickets", 2.4, true)]);
            odd = false;
            foreach (var x in q.Technicians)
                R.DataRow(t, odd = !odd, false, x.Name, R.Hrs(x.Hours), R.Hrs(x.BillableHours), x.Resolved.ToString(Inv), x.TicketsTouched.ToString(Inv));
        }

        R.Heading(section, "Longest-open tickets at the end of the period");
        if (q.OldestOpen.Count == 0)
        {
            section.AddParagraph("Nothing was open at the end of the period.").Format.Font.Color = R.Muted;
        }
        else
        {
            var t = R.DataTable(section, [("Ticket", 2.2, false), ("Title", 8.4, false), ("Priority", 2.4, false), ("Age", 2.4, true)]);
            odd = false;
            foreach (var o in q.OldestOpen)
                R.DataRow(t, odd = !odd, false, o.Reference, o.Title.Length > 70 ? o.Title[..67] + "…" : o.Title, o.Priority, $"{o.AgeDays} days");
        }

        var note = section.AddParagraph(
            "Raised counts tickets opened in the period. Resolved counts tickets resolved in the period, whenever they were opened. " +
            "SLA covers resolved tickets that had a target. Hours are time logged against this client's tickets in the period.");
        note.Format.Font.Size = 8;
        note.Format.Font.Color = R.Muted;
        note.Format.SpaceBefore = 16;

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    private static string Change(decimal now, decimal before)
    {
        if (before == 0) return now == 0 ? "no change" : "none the period before";
        var pct = (now - before) / before * 100;
        return pct == 0 ? "no change" : $"{(pct > 0 ? "+" : "−")}{Math.Abs(pct):0}% on the period before";
    }

    private static string Pct(QbrFigures f) => f.SlaPct is { } p ? $"{p.ToString("0.#", Inv)}% ({f.WithinSla}/{f.SlaEligible})" : "—";

    private static string Dur(double? hours) => hours switch
    {
        null => "—",
        < 48 => $"{hours.Value.ToString("0.#", Inv)} h",
        _ => $"{(hours.Value / 24).ToString("0.#", Inv)} days",
    };
}
