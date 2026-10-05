using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Desk.Infrastructure.Reporting;

/// <summary>
/// One flat sheet as an .xlsx file, on what .NET already ships: a workbook is a zip of a few XML
/// parts. Text is written as an inline string, which a spreadsheet shows and never evaluates, so a
/// value beginning "=" is text by construction; numbers are numeric cells. No formulas, links,
/// macros or external references are ever written.
/// </summary>
public static class XlsxWriter
{
    private const int MaxColumnWidth = 60;
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <param name="rows">Cells per row: a string is text, a number is a number, a bool is yes/no, null is an empty cell.</param>
    /// <param name="boldRows">Row indexes (0-based) shown in bold: the title and the column heads.</param>
    public static byte[] Write(string sheetName, IReadOnlyList<IReadOnlyList<object?>> rows, IReadOnlySet<int>? boldRows = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Part(zip, "[Content_Types].xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""");
            Part(zip, "_rels/.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Part(zip, "xl/workbook.xml",
                $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{Escape(SheetName(sheetName))}" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Part(zip, "xl/_rels/workbook.xml.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            // Cell styles: 0 plain, 1 bold, 2 a number to two places, 3 a whole number.
            Part(zip, "xl/styles.xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="4"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/><xf numFmtId="2" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="1" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs></styleSheet>""");
            Part(zip, "xl/worksheets/sheet1.xml", Sheet(rows, boldRows));
        }
        return stream.ToArray();
    }

    private static string Sheet(IReadOnlyList<IReadOnlyList<object?>> rows, IReadOnlySet<int>? boldRows)
    {
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        var widths = new int[columns];
        var body = new StringBuilder();
        for (var r = 0; r < rows.Count; r++)
        {
            var bold = boldRows?.Contains(r) == true;
            body.Append("<row r=\"").Append(r + 1).Append("\">");
            for (var c = 0; c < rows[r].Count; c++)
            {
                var value = rows[r][c];
                if (value is null) continue;
                var at = Column(c) + (r + 1).ToString(CultureInfo.InvariantCulture);
                var (number, text) = value switch
                {
                    string s => (null, s),
                    bool b => (null, b ? "yes" : "no"),
                    int or long or short or byte => (Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), (string?)null),
                    decimal m => (m.ToString(CultureInfo.InvariantCulture), null),
                    double d when double.IsFinite(d) => (d.ToString("R", CultureInfo.InvariantCulture), null),
                    float f when float.IsFinite(f) => (((double)f).ToString("R", CultureInfo.InvariantCulture), null),
                    IFormattable x => (null, x.ToString(null, CultureInfo.InvariantCulture)),
                    _ => ((string?)null, value.ToString() ?? ""),
                };
                if (number is not null)
                {
                    var whole = value is int or long or short or byte;
                    body.Append("<c r=\"").Append(at).Append("\" s=\"").Append(whole ? 3 : 2).Append("\"><v>").Append(number).Append("</v></c>");
                    // Only the first row of a title block is wide text; a number's width is its digits.
                    widths[c] = Math.Max(widths[c], number.Length + 2);
                }
                else
                {
                    // An inline string: shown as written. A spreadsheet never evaluates one, whatever it begins with.
                    body.Append("<c r=\"").Append(at).Append("\" t=\"inlineStr\"").Append(bold ? " s=\"1\"" : "").Append("><is><t xml:space=\"preserve\">").Append(Escape(text!)).Append("</t></is></c>");
                    // Lines above the table (title, filters) run across the sheet and should not widen the first columns.
                    if (rows[r].Count > 2 || c > 0 || text!.Length <= 28) widths[c] = Math.Max(widths[c], text!.Length + 2);
                }
            }
            body.Append("</row>");
        }

        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        if (columns > 0)
        {
            sb.Append("<cols>");
            for (var c = 0; c < columns; c++)
                sb.Append("<col min=\"").Append(c + 1).Append("\" max=\"").Append(c + 1).Append("\" width=\"").Append(Math.Clamp(widths[c], 8, MaxColumnWidth)).Append("\" customWidth=\"1\"/>");
            sb.Append("</cols>");
        }
        return sb.Append("<sheetData>").Append(body).Append("</sheetData></worksheet>").ToString();
    }

    private static void Part(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    /// <summary>A1-style column letters: 0 = A, 25 = Z, 26 = AA.</summary>
    public static string Column(int index)
    {
        var s = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }

    /// <summary>A sheet name a spreadsheet accepts: at most 31 characters, none of \ / ? * [ ] :.</summary>
    private static string SheetName(string name)
    {
        var clean = new string((name ?? "").Select(ch => "\\/?*[]:".Contains(ch) ? ' ' : ch).ToArray()).Trim().Trim('\'');
        if (clean.Length > 31) clean = clean[..31].TrimEnd();
        return clean.Length == 0 ? "Report" : clean;
    }

    /// <summary>XML-escaped, with the characters XML 1.0 cannot carry removed (a stray control character would make the file unreadable).</summary>
    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (char.IsHighSurrogate(ch) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(ch).Append(s[++i]); continue; }
            if (char.IsSurrogate(ch) || ch is '￾' or '￿') continue;
            if (ch < 0x20 && ch is not ('\t' or '\n' or '\r')) continue;
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }
}
