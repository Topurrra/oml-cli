using System.Globalization;

namespace OmlCli;

// Change history the model keeps on most top-level elements (CreatedBy, LastModifiedBy, LastModifiedDate, LastMergedBy, LastMergedDate).
// Read by reflection because no single interface declares them. An element without the properties has no history.
static class History
{
    // OutSystems uses 1900-01-01 as the empty date; a CLR default is 0001-01-01.
    static readonly DateTime EmptyDate = new(1900, 1, 1);

    static readonly string[] Fields = ["CreatedBy", "LastModifiedBy", "LastModifiedDate", "LastMergedBy", "LastMergedDate"];

    // Specs may not write these: authorship is Service Studio's to record.
    public static bool IsField(string name) => Fields.Contains(name, StringComparer.OrdinalIgnoreCase);

    public sealed record Info(string? createdBy, string? lastModifiedBy, string? lastModifiedDate, string? lastMergedBy, string? lastMergedDate);

    static string? Text(object o, string name) => Reflect.Property(o, name)?.GetValue(o) is string { Length: > 0 } s ? s : null;

    // ISO 8601 without a time zone: the model stores the date as written and does not say which zone it is in, so it is not converted.
    static string? Date(object o, string name) =>
        Reflect.Property(o, name)?.GetValue(o) is DateTime d && d > EmptyDate ? d.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) : null;

    public static Info? Of(object o)
    {
        if (Reflect.Property(o, "LastModifiedDate") is null) return null;
        try
        {
            var info = new Info(Text(o, "CreatedBy"), Text(o, "LastModifiedBy"), Date(o, "LastModifiedDate"), Text(o, "LastMergedBy"), Date(o, "LastMergedDate"));
            return info == new Info(null, null, null, null, null) ? null : info;
        }
        // System elements ((System) structures such as Attachment, the Anonymous and Registered roles) declare the
        // fields but keep no history: their getters throw InvalidOperationException or NotImplementedException.
        catch (System.Reflection.TargetInvocationException e) when (e.InnerException is InvalidOperationException or NotImplementedException) { return null; }
    }

    // "Changed by <x> on <date>; created by <y>", or null when the model holds none of it.
    public static string? Line(Info? h)
    {
        if (h is null) return null;
        var parts = new List<string>();
        if (h.lastModifiedBy is not null || h.lastModifiedDate is not null)
            parts.Add("Changed" + (h.lastModifiedBy is null ? "" : " by " + h.lastModifiedBy) + (h.lastModifiedDate is null ? "" : " on " + h.lastModifiedDate));
        if (h.createdBy is not null) parts.Add("created by " + h.createdBy);
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    // Validates --changed-since and returns it as yyyy-MM-dd.
    public static string ParseSince(string value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : throw new CliError($"--changed-since needs a date as yyyy-mm-dd, got '{value}'.", 2);

    // What `inspect` adds to a listed element: its last change, and a filter on the date of that change.
    // `since` is yyyy-MM-dd and the dates are ISO 8601, so the first ten characters compare as text.
    // An element without a modified date never passes the filter.
    public sealed class View(bool show, string? since)
    {
        // One reflection pass per element, however many of By, On and Pass ask.
        readonly Dictionary<object, Info?> seen = new(ReferenceEqualityComparer.Instance);
        Info? Get(object o) => seen.TryGetValue(o, out var info) ? info : seen[o] = Of(o);
        public string? By(object o) => show ? Get(o)?.lastModifiedBy : null;
        public string? On(object o) => show ? Get(o)?.lastModifiedDate : null;
        public bool Pass(object o) => since is null || Get(o)?.lastModifiedDate is { } d && string.CompareOrdinal(d, 0, since, 0, 10) >= 0;
        public IEnumerable<T> Keep<T>(IEnumerable<T> items) where T : notnull => since is null ? items : items.Where(i => Pass(i));
    }
}
