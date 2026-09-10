using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VersaCore;

// Experimental comparison pipeline. The model extracts local signals only;
// this class adjudicates a deliberately small set of intrinsic issues in code.
internal sealed class SensorIntrinsicAuditPipeline
{
    public async Task<SensorIntrinsicAuditExecution> RunAsync(SensorIntrinsicAuditInput input)
    {
        if (string.IsNullOrWhiteSpace(AppConstants.ApiKey)) return Failure("OpenAI API key is not configured.");
        try
        {
            var payload = new
            {
                model = AppConstants.SensorIntrinsicAuditModel,
                temperature = 0,
                response_format = new { type = "json_schema", json_schema = new { name = "intrinsic_sensor", strict = true, schema = JsonNode.Parse(Schema) } },
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = $"pageLanguage: {input.PageLanguage}\npageTitle: {input.Title}\npageContent:\n{input.PageContent}" }
                }
            };
            using var client = new HttpClient { BaseAddress = new Uri(AppConstants.BaseUrl), Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AppConstants.ApiKey);
            using var response = await client.PostAsync("chat/completions", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
            var raw = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) return Failure($"OpenAI returned HTTP {(int)response.StatusCode}: {raw}");

            using var completion = JsonDocument.Parse(raw);
            var text = completion.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";
            var sensor = JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
            var visibleText = Normalize(input.PageContent);
            var rejectedSignals = new JsonArray();
            var issues = Adjudicate(input, sensor, visibleText, rejectedSignals);
            var analysis = new JsonObject
            {
                ["taxonomyVersion"] = "intrinsic-sensor-v1",
                ["sensor"] = sensor,
                ["issues"] = issues,
                ["rejectedSignals"] = rejectedSignals,
                ["classification"] = new JsonObject
                {
                    ["type"] = issues.Count == 0 ? "healthy" : "unhealthy",
                    ["confidence"] = issues.Count == 0 ? 0.8 : 0.95,
                    ["insufficientEvidence"] = false
                },
                ["externalVerification"] = new JsonObject { ["enabled"] = false, ["skipped"] = true }
            };
            using var document = JsonDocument.Parse(analysis.ToJsonString());
            return new SensorIntrinsicAuditExecution(document.RootElement.Clone(), AppConstants.SensorIntrinsicAuditModel, raw, null);
        }
        catch (Exception exception) { return Failure(exception.ToString()); }
    }

    internal static SensorIntrinsicAuditExecution CaptureUnavailable(string reason) => Failure(reason);

    private static JsonArray Adjudicate(SensorIntrinsicAuditInput input, JsonObject sensor, string visibleText, JsonArray rejected)
    {
        var issues = new JsonArray();
        var languageSignal = sensor["mainContentLanguage"]?.AsObject();
        var language = ReadString(languageSignal, "language");
        var languageQuote = ReadString(languageSignal, "quote");
        if (LanguageDiffers(input.PageLanguage, language) && QuoteExists(visibleText, languageQuote))
        {
            issues.Add(Issue("language_mismatch", $"The page declares language '{input.PageLanguage}', while its main reader-facing content is '{language}'.", [languageQuote]));
        }

        var visibleTitle = sensor["visibleTitle"]?.AsObject();
        var primaryBody = sensor["primaryBody"]?.AsObject();
        var titleTopic = ReadString(visibleTitle, "topic");
        var bodyTopic = ReadString(primaryBody, "topic");
        var titleQuote = ReadString(visibleTitle, "quote");
        var bodyQuote = ReadString(primaryBody, "quote");
        var bodyRelation = ReadString(primaryBody, "relationToVisibleTitle");
        if (KnownTopic(titleTopic) && KnownTopic(bodyTopic) && bodyRelation == "different_subject")
        {
            if (QuotesExist(visibleText, titleQuote, bodyQuote))
            {
                issues.Add(Issue("title_body_mismatch", $"The visible page title is about {Humanize(titleTopic)}, while the main body is about {Humanize(bodyTopic)}.", [titleQuote, bodyQuote]));
            }
            else
            {
                Reject(rejected, "title_body_topics", "quotes_not_found_in_snapshot", [titleQuote, bodyQuote]);
            }
        }

        foreach (var signal in sensor["riskSignals"]?.AsArray()?.OfType<JsonObject>() ?? [])
        {
            var type = ReadString(signal, "type");
            var quote = ReadString(signal, "quote");
            if (!QuoteExists(visibleText, quote)) { Reject(rejected, type, "quote_not_found_in_snapshot", [quote]); continue; }
            var code = type == "unsafe_sensitive_guidance" ? "sensitive_topic_without_context" : "brand_safety_risk";
            var reason = type == "unsafe_sensitive_guidance"
                ? "The page gives guidance about a sensitive safety topic without appropriate escalation or care context."
                : "The page contains explicit wording or instructions that create a brand safety risk.";
            AddEvidence(issues, code, reason, quote);
        }
        return issues;
    }

    private static JsonObject Issue(string code, string reason, IEnumerable<string> quotes) => new()
    {
        ["code"] = code,
        ["reason"] = reason,
        ["pageQuotes"] = new JsonArray(quotes.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => JsonValue.Create(value)!).ToArray()),
        ["source"] = "sensor"
    };

    private static void AddEvidence(JsonArray issues, string code, string reason, string quote)
    {
        var existing = issues.OfType<JsonObject>().FirstOrDefault(issue => ReadString(issue, "code") == code);
        if (existing is null) { issues.Add(Issue(code, reason, [quote])); return; }
        var quotes = existing["pageQuotes"]?.AsArray() ?? new JsonArray();
        if (quotes.All(item => !string.Equals(item?.GetValue<string>(), quote, StringComparison.Ordinal))) quotes.Add(quote);
        existing["pageQuotes"] = quotes;
    }

    private static void Reject(JsonArray rejected, string signal, string reason, IEnumerable<string> quotes) => rejected.Add(new JsonObject
    {
        ["signal"] = signal,
        ["rejectionReason"] = reason,
        ["pageQuotes"] = new JsonArray(quotes.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => JsonValue.Create(value)!).ToArray())
    });

    private static bool QuotesExist(string visibleText, params string[] quotes) => quotes.All(quote => QuoteExists(visibleText, quote));
    private static bool QuoteExists(string visibleText, string quote) => !string.IsNullOrWhiteSpace(quote) && visibleText.Contains(Normalize(quote), StringComparison.Ordinal);
    private static bool KnownTopic(string value) => value is not "" and not "unknown";
    private static string Humanize(string value) => value.Replace('_', ' ');
    private static string Normalize(string value) => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string ReadString(JsonObject? node, string property) => node?[property]?.GetValue<string>()?.Trim() ?? string.Empty;
    private static bool LanguageDiffers(string declared, string observed) => PrimaryLanguage(declared) is { Length: > 0 } expected && PrimaryLanguage(observed) is { Length: > 0 } actual && expected != actual;
    private static string PrimaryLanguage(string value) => value.Split('-', '_')[0].Trim().ToLowerInvariant() is var language && language is not ("" or "unknown" or "und") ? language : string.Empty;

    private static SensorIntrinsicAuditExecution Failure(string error)
    {
        using var document = JsonDocument.Parse($"{{\"classification\":{{\"type\":\"inconclusive\",\"confidence\":0,\"insufficientEvidence\":true}},\"issues\":[],\"pipelineError\":{JsonSerializer.Serialize(error)}}}");
        return new SensorIntrinsicAuditExecution(document.RootElement.Clone(), AppConstants.SensorIntrinsicAuditModel, error, error);
    }

    private const string SystemPrompt = """
        Role
        Extract only local, literal signals from one page snapshot. Do not diagnose the page, emit issue codes, browse, use outside knowledge, evaluate facts, or infer that old content is defective. Return only JSON matching the supplied schema.

        Evidence rules
        Work only from pageContent. Every quote must be an exact, contiguous substring copied literally from pageContent. Never use ellipses, paraphrase, brackets, or joined text. Do not use text after a [site context] marker as evidence.

        Topic signals
        mainContentLanguage is the dominant reader-facing language, using a primary BCP-47 tag such as en, es, pt, or no. Its quote must be a substantial sentence or paragraph in that language from main content; never use a short CTA, a navigation label, or a text fragment. Do not let a short translation outweigh the dominant long-form content.
        visibleTitle is the first reader-facing H1 or equivalent page title. Its topic is a short normalized lower_snake_case subject. Use unknown when no visible title exists.
        primaryBody is the substantive explanatory body that follows or supports the visible title, ignoring navigation, repeated cards, generic calls to action, and site chrome. Its topic is a short normalized lower_snake_case subject. relationToVisibleTitle is same_subject when the body explains, expands, applies, or offers a service around the title subject; different_subject only when it instead centers on another subject; otherwise unclear. Do not make two topics different merely because one is more specific, contains a brand, or includes a related service. Use unknown when the body topic cannot be identified.

        Risk signals
        Emit a risk signal only when its quote itself proves one of these types:
        - unsafe_sensitive_guidance: operational guidance about self-harm, abuse, imminent harm, or another safety risk that dismisses care, escalation, boundaries, or safety.
        - explicit_objectification
        - explicit_discrimination
        - explicit_dehumanisation
        - explicit_humiliation
        - explicit_coercion
        Do not emit a risk signal for poor grammar, repetition, generic marketing, informal prose, vague quality criticism, or merely old content. Prefer no signal when uncertain.
        """;

    private const string Schema = """
        {"type":"object","additionalProperties":false,"required":["mainContentLanguage","visibleTitle","primaryBody","riskSignals"],"properties":{"mainContentLanguage":{"type":"object","additionalProperties":false,"required":["language","quote"],"properties":{"language":{"type":"string"},"quote":{"type":"string"}}},"visibleTitle":{"type":"object","additionalProperties":false,"required":["topic","quote"],"properties":{"topic":{"type":"string"},"quote":{"type":"string"}}},"primaryBody":{"type":"object","additionalProperties":false,"required":["topic","quote","relationToVisibleTitle"],"properties":{"topic":{"type":"string"},"quote":{"type":"string"},"relationToVisibleTitle":{"type":"string","enum":["same_subject","different_subject","unclear"]}}},"riskSignals":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["type","quote"],"properties":{"type":{"type":"string","enum":["unsafe_sensitive_guidance","explicit_objectification","explicit_discrimination","explicit_dehumanisation","explicit_humiliation","explicit_coercion"]},"quote":{"type":"string"}}}}}}
        """;
}

internal sealed record SensorIntrinsicAuditInput(string FinalUrl, string Title, string PageLanguage, string PageContent, string OutputLanguage);
internal sealed record SensorIntrinsicAuditExecution(JsonElement Analysis, string Model, string RawResponse, string? Error);
