namespace VersaCore;

internal static class AppConstants
{
    // Switch Provider to "anthropic" to use Claude models
    public static readonly string Provider = Environment.GetEnvironmentVariable("VERSA_PROVIDER") ?? "openai";

    // OpenAI
    public static readonly string IntentModel = Environment.GetEnvironmentVariable("VERSA_INTENT_MODEL") ?? "gpt-4.1-mini";
    public static readonly string DiagnosisModel = Environment.GetEnvironmentVariable("VERSA_DIAGNOSIS_MODEL") ?? "gpt-4.1-mini";
    // The intrinsic auditor is one local-only model call over the page snapshot.
    public static readonly string IntrinsicAuditModel = Environment.GetEnvironmentVariable("VERSA_INTRINSIC_AUDIT_MODEL") ?? "gpt-5.4-mini";
    public static readonly string SensorIntrinsicAuditModel = Environment.GetEnvironmentVariable("VERSA_SENSOR_INTRINSIC_AUDIT_MODEL") ?? "gpt-5.4-mini";
    public static readonly string TaxonomyDiscoveryModel = Environment.GetEnvironmentVariable("VERSA_TAXONOMY_DISCOVERY_MODEL") ?? "gpt-5.4-mini";
    public static readonly string VerificationSelectionModel = Environment.GetEnvironmentVariable("VERSA_VERIFICATION_SELECTION_MODEL") ?? "gpt-5.4-mini";
    public static readonly string FactCheckModel = Environment.GetEnvironmentVariable("VERSA_FACT_CHECK_MODEL") ?? "gpt-5.4-mini";
    public static readonly string RecommendationModel = Environment.GetEnvironmentVariable("VERSA_RECOMMENDATION_MODEL") ?? "gpt-4.1-mini";
    public static readonly string ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
    public static readonly string BaseUrl = "https://api.openai.com/v1/";

    // Fact-check evidence retrieval. Keep this false to use OpenAI web search.
    // When true, Tavily retrieves source content and OpenAI evaluates only that evidence packet.
    public static readonly bool UseExternalEvidenceSearch =
        bool.TryParse(Environment.GetEnvironmentVariable("VERSA_USE_EXTERNAL_EVIDENCE_SEARCH"), out var useExternalEvidenceSearch)
        && useExternalEvidenceSearch;
    public static readonly string TavilyApiKey = Environment.GetEnvironmentVariable("TAVILY_API_KEY") ?? string.Empty;
    public static readonly string TavilyBaseUrl = "https://api.tavily.com/";

    // The secondary pipeline can use OpenAI Web Search as its evidence provider.
    // Keep Tavily selectable while we compare quality and cost on the benchmark.
    public static readonly string SecondaryEvidenceProvider =
        Environment.GetEnvironmentVariable("VERSA_SECONDARY_EVIDENCE_PROVIDER") ?? "openai_web";
    public static readonly string SecondaryWebSearchModel =
        Environment.GetEnvironmentVariable("VERSA_SECONDARY_WEB_SEARCH_MODEL") ?? "gpt-5.6-terra";

    // Anthropic — set Provider = "anthropic" and fill AnthropicApiKey to use Claude
    public static readonly string AnthropicModel = Environment.GetEnvironmentVariable("VERSA_ANTHROPIC_MODEL") ?? "claude-haiku-4-5-20251001";
    public static readonly string AnthropicApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? string.Empty;
    public static readonly string AnthropicBaseUrl = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL") ?? "https://api.anthropic.com/v1/";

    public const int NavigationTimeoutMs = 45000;
    public const int PostDomContentLoadedDelayMs = 700;
    public const int ScrollPasses = 2;
    public const int ScrollDelayMs = 500;
    public const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36";
    public const int MaxPreviewChars = 2500;
    public const int StructuralOutlineMaxEstimatedTokens = 30000;

    public static readonly string OutputRoot = Path.Combine(
        AppContext.BaseDirectory,
        "output");
}
