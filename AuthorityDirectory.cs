namespace VersaCore;

// This is deliberately a directory of sources, not a knowledge base. It defines
// where evidence may be retrieved for a subject area; facts remain live and are
// always taken from the extracted source text.
internal static class AuthorityDirectory
{
    internal static readonly AuthorityCollection[] Collections =
    [
        new(
            "es-national-law",
            "Spanish national legislation and public-administration procedures",
            "ES",
            "es",
            ["boe.es", "administracion.gob.es"]),
        new(
            "es-immigration-nationality",
            "Spanish immigration, nationality, consular, and citizenship procedures",
            "ES",
            "es",
            ["mjusticia.gob.es", "exteriores.gob.es", "inclusion.gob.es"]),
        new(
            "es-medicines",
            "Spanish medicine approvals, product information, and safety notices",
            "ES",
            "es",
            ["aemps.gob.es"]),
        new(
            "us-medicines",
            "United States medicine approvals, product information, and safety notices",
            "US",
            "en",
            ["fda.gov", "accessdata.fda.gov"])
    ];

    internal static AuthorityCollection[] Find(IEnumerable<string> ids) =>
        Collections.Where(collection => ids.Contains(collection.Id, StringComparer.Ordinal)).ToArray();
}

internal sealed record AuthorityCollection(
    string Id,
    string Description,
    string Jurisdiction,
    string Language,
    string[] Domains);

internal sealed record SecondaryAuthorityRoute(
    string AssertionId,
    string[] CollectionIds,
    string Reason)
{
    public AuthorityCollection[] Collections => AuthorityDirectory.Find(CollectionIds);
    public string[] Domains => Collections.SelectMany(collection => collection.Domains)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
