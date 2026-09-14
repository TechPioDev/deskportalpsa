using System.Globalization;
using System.Text;
using Desk.Application.Reporting;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace Desk.Infrastructure.Reporting;

/// <summary>CSV and PDF renderings of a <see cref="TechnicianReport"/>. Both read the same model; neither computes.</summary>
public static class TechnicianReportRenderer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string ToCsv(TechnicianReport r)
    {
        var sb = new StringBuilder();
        void Line(params object?[] cells) => sb.Append(string.Join(',', cells.Select(Cell))).Append("\r\n");

        Line(r.Title);
        Line("Organization", r.OrganizationName);
        Line("Period", r.PeriodStart.ToString("yyyy-MM-dd", Inv), r.PeriodEnd.ToString("yyyy-MM-dd", Inv), r.TimeZone);
        Line("Client", r.ClientName ?? "All clients");
        Line("Generated", r.GeneratedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", Inv));
        Line();
        Line("Technician", "Hours", "Billable hours", "Resolved", "Tickets touched", "Hours per ticket", "Active days");
        foreach (var t in r.Technicians)
            Line(t.Name, t.Hours, t.BillableHours, t.Resolved, t.TicketsTouched, t.HoursPerTicket, t.ActiveDays);
        Line("Total", r.TotalHours, r.TotalBillable, r.TotalResolved, r.TotalTouched,
            r.TotalTouched > 0 ? Math.Round(r.TotalHours / r.TotalTouched, 2) : null, null);

        if (r.Days.Count > 1)
        {
            Line();
            Line("Date", "Hours", "Resolved");
            foreach (var d in r.Days) Line(d.Date.ToString("yyyy-MM-dd", Inv), d.Hours, d.Resolved);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Quoted where needed, and a leading formula character neutralised — technician names come from
    /// the PSA, and a name beginning "=" would otherwise run as a formula when the file is opened.
    /// </summary>
    internal static string Cell(object? value)
    {
        var s = value switch
        {
            null => "",
            decimal d => d.ToString("0.##", Inv),
            IFormattable f => f.ToString(null, Inv),
            _ => value.ToString() ?? "",
        };
        if (s.Length > 0 && value is string && "=+-@".Contains(s[0])) s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }

    // ---------------------------------------------------------------------------------------------

    internal const string Font = "Source Sans 3";
    internal static readonly Color Ink = new(0x1f, 0x29, 0x37);
    internal static readonly Color Muted = new(0x6b, 0x72, 0x80);
    internal static readonly Color Rule = new(0xe5, 0xe7, 0xeb);
    internal static readonly Color Band = new(0xf3, 0xf4, 0xf6);
    internal static readonly Color Accent = new(0x1d, 0x4e, 0xd8);

    public static byte[] ToPdf(TechnicianReport r)
    {
        EmbeddedFonts.EnsureRegistered();

        var doc = new Document { Info = { Title = r.Title, Author = r.OrganizationName } };
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = Font;
        normal.Font.Size = 9.5;
        normal.Font.Color = Ink;

        var section = doc.AddSection();
        section.PageSetup = doc.DefaultPageSetup.Clone();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(1.8);
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.6);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.8);

        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 8;
        footer.Format.Font.Color = Muted;
        footer.AddText($"{r.OrganizationName} · generated {r.GeneratedAt.UtcDateTime.ToString("d MMM yyyy HH:mm", Inv)} UTC · page ");
        footer.AddPageField();
        footer.AddText(" of ");
        footer.AddNumPagesField();

        var eyebrow = section.AddParagraph(r.OrganizationName.ToUpperInvariant());
        eyebrow.Format.Font.Size = 8;
        eyebrow.Format.Font.Color = Accent;
        eyebrow.Format.Font.Bold = true;

        var title = section.AddParagraph("Technician productivity");
        title.Format.Font.Size = 20;
        title.Format.Font.Bold = true;
        title.Format.SpaceBefore = 2;

        var sub = section.AddParagraph(
            $"{r.PeriodLabel} · {r.PeriodStart.ToString("d MMM yyyy", Inv)} – {r.PeriodEnd.ToString("d MMM yyyy", Inv)} ({r.TimeZone})" +
            (r.ClientName is null ? " · all clients" : $" · {r.ClientName}"));
        sub.Format.Font.Color = Muted;
        sub.Format.SpaceAfter = 14;

        // Headline figures, one row of four.
        var tiles = section.AddTable();
        tiles.Borders.Visible = false;
        for (var i = 0; i < 4; i++) tiles.AddColumn(Unit.FromCentimeter(4.35));
        var labels = tiles.AddRow();
        var values = tiles.AddRow();
        (string Label, string Value, string Note)[] figures =
        [
            ("Hours logged", r.TotalHours.ToString("0.##", Inv) + "h", $"{r.BillablePct}% billable"),
            ("Tickets resolved", r.TotalResolved.ToString(Inv), $"{r.TotalTouched} tickets worked"),
            ("Hours per ticket", r.TotalTouched > 0 ? (r.TotalHours / r.TotalTouched).ToString("0.##", Inv) + "h" : "—", "across tickets worked"),
            ("People with activity", r.Technicians.Count.ToString(Inv), "logged time or resolved"),
        ];
        for (var i = 0; i < 4; i++)
        {
            var l = labels.Cells[i].AddParagraph(figures[i].Label);
            l.Format.Font.Size = 8;
            l.Format.Font.Color = Muted;
            var v = values.Cells[i].AddParagraph(figures[i].Value);
            v.Format.Font.Size = 17;
            v.Format.Font.Bold = true;
            var n = values.Cells[i].AddParagraph(figures[i].Note);
            n.Format.Font.Size = 8;
            n.Format.Font.Color = Muted;
        }

        Heading(section, "By technician");
        if (r.Technicians.Count == 0)
        {
            section.AddParagraph("No time was logged and nothing was resolved in this period.").Format.Font.Color = Muted;
        }
        else
        {
            var t = DataTable(section, [("Technician", 5.2, false), ("Hours", 1.8, true), ("Billable", 1.8, true),
                ("Resolved", 1.8, true), ("Tickets", 1.8, true), ("Hrs/ticket", 2.0, true), ("Active days", 2.0, true)]);
            var odd = false;
            foreach (var x in r.Technicians)
                DataRow(t, odd = !odd, false, x.Name, Hrs(x.Hours), Hrs(x.BillableHours), x.Resolved.ToString(Inv),
                    x.TicketsTouched.ToString(Inv), x.HoursPerTicket is { } h ? Hrs(h) : "—", x.ActiveDays.ToString(Inv));
            DataRow(t, false, true, "Total", Hrs(r.TotalHours), Hrs(r.TotalBillable), r.TotalResolved.ToString(Inv),
                r.TotalTouched.ToString(Inv), r.TotalTouched > 0 ? Hrs(r.TotalHours / r.TotalTouched) : "—", "");
        }

        if (r.Days.Count > 1)
        {
            Heading(section, "By day");
            var t = DataTable(section, [("Date", 5.2, false), ("Hours", 2.4, true), ("Resolved", 2.4, true)]);
            var odd = false;
            foreach (var d in r.Days)
                DataRow(t, odd = !odd, false, d.Date.ToString("ddd d MMM yyyy", Inv), Hrs(d.Hours), d.Resolved.ToString(Inv));
        }

        var note = section.AddParagraph(
            "Read these figures together, never one alone: hours reward whoever is slowest and resolved counts reward whoever takes " +
            "the easiest tickets. They cover work recorded in the portal and synced from the PSA; anything done elsewhere is not counted.");
        note.Format.Font.Size = 8;
        note.Format.Font.Color = Muted;
        note.Format.SpaceBefore = 16;

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    internal static string Hrs(decimal h) => h.ToString("0.##", Inv);

    internal static void Heading(Section s, string text)
    {
        var p = s.AddParagraph(text);
        p.Format.Font.Size = 11.5;
        p.Format.Font.Bold = true;
        p.Format.SpaceBefore = 18;
        p.Format.SpaceAfter = 6;
        p.Format.KeepWithNext = true;
    }

    internal static Table DataTable(Section s, (string Header, double Cm, bool Right)[] columns)
    {
        var t = s.AddTable();
        t.Borders.Visible = false;
        t.Rows.LeftIndent = 0;
        foreach (var c in columns)
        {
            var col = t.AddColumn(Unit.FromCentimeter(c.Cm));
            col.Format.Alignment = c.Right ? ParagraphAlignment.Right : ParagraphAlignment.Left;
        }
        var head = t.AddRow();
        head.HeadingFormat = true; // repeats on every page the table spans
        head.Format.Font.Size = 8;
        head.Format.Font.Color = Muted;
        head.Borders.Bottom.Color = Rule;
        head.Borders.Bottom.Width = 0.75;
        for (var i = 0; i < columns.Length; i++)
        {
            head.Cells[i].AddParagraph(columns[i].Header);
            head.Cells[i].VerticalAlignment = VerticalAlignment.Bottom;
        }
        return t;
    }

    internal static void DataRow(Table t, bool shaded, bool total, params string[] cells)
    {
        var row = t.AddRow();
        row.TopPadding = row.BottomPadding = 2.5;
        if (shaded) row.Shading.Color = Band;
        if (total)
        {
            row.Format.Font.Bold = true;
            row.Borders.Top.Color = Ink;
            row.Borders.Top.Width = 0.75;
        }
        for (var i = 0; i < cells.Length; i++) row.Cells[i].AddParagraph(cells[i]);
    }
}

/// <summary>
/// Serves the report font from the assembly. PDFsharp finds no system fonts in the Linux containers,
/// so without a resolver every PDF would fail to render there even though it works on a dev PC.
/// </summary>
internal sealed class EmbeddedFonts : IFontResolver
{
    private static readonly object Gate = new();
    private static bool _registered;

    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered) return;
            // Another component may already have set a resolver; fall back to it for other families.
            GlobalFontSettings.FontResolver ??= new EmbeddedFonts();
            _registered = true;
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        // Every family resolves to Source Sans: MigraDoc asks for its own defaults (e.g. "Courier New"
        // for some fields), and a missing face must not throw halfway through a report.
        => new(isBold ? "SourceSans3-Bold" : "SourceSans3-Regular");

    public byte[]? GetFont(string faceName)
    {
        using var stream = typeof(EmbeddedFonts).Assembly.GetManifestResourceStream($"Desk.Fonts.{faceName}.ttf");
        if (stream is null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
