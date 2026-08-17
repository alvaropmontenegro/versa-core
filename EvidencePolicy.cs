namespace VersaCore;

internal enum ClaimType
{
    LegalRegulatory,
    AvailabilityStatus,
    ProductSupport,
    Award,
    Event,
    Corporate,
    Numeric,
    Other
}

internal enum ClaimTimeScope { Historical, Current, Future, Timeless }
internal enum ClaimVolatility { Stable, Changing }
internal enum ClaimRisk { Standard, HighStakes }
internal enum MinimumSourceTier { OfficialOrPrimary, PrimaryPreferred }

internal sealed record EvidencePolicy(
    MinimumSourceTier MinimumSourceTier,
    bool RequiresCurrentSource,
    IReadOnlyList<string> PreferredAuthorityKinds);

internal static class EvidencePolicies
{
    private static readonly IReadOnlyDictionary<ClaimType, EvidencePolicy> ByClaimType =
        new Dictionary<ClaimType, EvidencePolicy>
        {
            [ClaimType.LegalRegulatory] = new(MinimumSourceTier.OfficialOrPrimary, true,
                ["government", "regulator", "official_legislation"]),
            [ClaimType.AvailabilityStatus] = new(MinimumSourceTier.OfficialOrPrimary, true,
                ["vendor", "organizer", "official_service"]),
            [ClaimType.ProductSupport] = new(MinimumSourceTier.OfficialOrPrimary, true,
                ["vendor_documentation", "vendor_lifecycle_policy"]),
            [ClaimType.Award] = new(MinimumSourceTier.OfficialOrPrimary, false,
                ["award_organizer", "official_winner_directory"]),
            [ClaimType.Event] = new(MinimumSourceTier.OfficialOrPrimary, true,
                ["event_organizer", "official_event_site"]),
            [ClaimType.Corporate] = new(MinimumSourceTier.PrimaryPreferred, false,
                ["company_newsroom", "filing", "original_report"]),
            [ClaimType.Numeric] = new(MinimumSourceTier.OfficialOrPrimary, false,
                ["dataset_owner", "original_report", "responsible_organization"]),
            [ClaimType.Other] = new(MinimumSourceTier.OfficialOrPrimary, false,
                ["responsible_organization"])
        };

    public static EvidencePolicy Resolve(ClaimType claimType, ClaimTimeScope timeScope, ClaimRisk risk) =>
        ByClaimType[claimType] with
        {
            RequiresCurrentSource = ByClaimType[claimType].RequiresCurrentSource
                || timeScope is ClaimTimeScope.Current or ClaimTimeScope.Future
                || risk == ClaimRisk.HighStakes
        };

    public static ClaimType ParseClaimType(string? value) => value switch
    {
        "legal_regulatory" => ClaimType.LegalRegulatory,
        "availability_status" => ClaimType.AvailabilityStatus,
        "product_support" => ClaimType.ProductSupport,
        "award" => ClaimType.Award,
        "event" => ClaimType.Event,
        "corporate" => ClaimType.Corporate,
        "numeric" => ClaimType.Numeric,
        _ => ClaimType.Other
    };

    public static ClaimTimeScope ParseTimeScope(string? value) => value switch
    {
        "historical" => ClaimTimeScope.Historical,
        "current" => ClaimTimeScope.Current,
        "future" => ClaimTimeScope.Future,
        _ => ClaimTimeScope.Timeless
    };

    public static ClaimRisk ParseRisk(string? value) =>
        value == "high_stakes" ? ClaimRisk.HighStakes : ClaimRisk.Standard;
}
