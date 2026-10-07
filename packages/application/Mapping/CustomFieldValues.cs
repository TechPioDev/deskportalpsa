using System.Text.Json;

namespace Desk.Application.Mapping;

/// <summary>
/// The custom field values a ticket holds, as they are stored: one JSON object of field key to
/// value, holding only fields an administrator chose to bring in, and only those with a value.
///
/// Written the same way every time (keys in ordinal order), so that the same values are the same
/// text and a ticket read again unchanged is seen to be unchanged.
/// </summary>
public static class CustomFieldValues
{
    public const int MaxValueLength = 2000;

    /// <summary>
    /// What to keep of what a PSA sent: the fields chosen for import that have a value. Null where
    /// that is nothing, so a ticket with no imported field holds nothing at all.
    /// </summary>
    public static string? Keep(IReadOnlyDictionary<string, string?> sent, IReadOnlySet<string> imported)
    {
        if (sent.Count == 0 || imported.Count == 0) return null;
        var kept = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in sent)
        {
            if (!imported.Contains(key) || string.IsNullOrWhiteSpace(value)) continue;
            var text = value.Trim();
            kept[key] = text.Length <= MaxValueLength ? text : text[..MaxValueLength];
        }
        return kept.Count == 0 ? null : JsonSerializer.Serialize(kept);
    }

    /// <summary>The stored values, by field key. Empty for nothing stored, and for anything that does not read as such an object.</summary>
    public static IReadOnlyDictionary<string, string> Read(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return Empty;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stored) ?? Empty;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();
}
