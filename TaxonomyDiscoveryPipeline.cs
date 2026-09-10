using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VersaCore;

// Internal research tool. It creates evidence-backed review cards and never
// emits a product verdict or a canonical issue code.
internal sealed class TaxonomyDiscoveryPipeline
{
    public async Task<TaxonomyDiscoveryExecution> RunAsync(TaxonomyDiscoveryInput input)
    {
        if (string.IsNullOrWhiteSpace(AppConstants.ApiKey)) return Failure("OpenAI API key is not configured.");
        try
        {
            var payload = new
            {
                model = AppConstants.TaxonomyDiscoveryModel,
                temperature = 0,
                response_format = new { type = "json_schema", json_schema = new { name = "taxonomy_discovery", strict = true, schema = JsonNode.Parse(Schema) } },
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = $"documentTitle: {input.Title}\nurl: {input.FinalUrl}\ndeclaredLanguage: {input.PageLanguage}\npageContent:\n{input.PageContent}" }
                }
            };
            using var client = new HttpClient { BaseAddress = new Uri(AppConstants.BaseUrl), Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AppConstants.ApiKey);
            using var response = await client.PostAsync("chat/completions", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
            var raw = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) return Failure($"OpenAI returned HTTP {(int)response.StatusCode}: {raw}");

            using var completion = JsonDocument.Parse(raw);
            var text = completion.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";
            var root = JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
            var visible = Normalize(input.PageContent);
            var rejected = new JsonArray();
            ValidateCards(root["reviewObservations"]?.AsArray() ?? [], visible, rejected, "reviewObservations");
            ValidateCards(root["externalQuestions"]?.AsArray() ?? [], visible, rejected, "externalQuestions");
            root["rejectedCards"] = rejected;
            root["taxonomyVersion"] = "taxonomy-discovery-v1";
            root["classification"] = new JsonObject { ["type"] = "discovery", ["confidence"] = 0, ["insufficientEvidence"] = false };
            root["issues"] = new JsonArray();
            root["externalVerification"] = new JsonObject { ["enabled"] = false, ["skipped"] = true };
            using var document = JsonDocument.Parse(root.ToJsonString());
            return new TaxonomyDiscoveryExecution(document.RootElement.Clone(), AppConstants.TaxonomyDiscoveryModel, raw, null);
        }
        catch (Exception exception) { return Failure(exception.ToString()); }
    }

    internal static TaxonomyDiscoveryExecution CaptureUnavailable(string reason) => Failure(reason);

    private static void ValidateCards(JsonArray cards, string visible, JsonArray rejected, string collection)
    {
        for (var index = cards.Count - 1; index >= 0; index--)
        {
            var card = cards[index]?.AsObject();
            var quotes = card?["evidence"]?.AsArray() ?? [];
            var valid = quotes.Count > 0 && quotes.All(item => item is JsonValue value && visible.Contains(Normalize(value.GetValue<string>()), StringComparison.Ordinal));
            if (valid) continue;
            rejected.Add(new JsonObject { ["collection"] = collection, ["card"] = card?.DeepClone(), ["rejectionReason"] = "evidence_not_found_in_snapshot" });
            cards.RemoveAt(index);
        }
    }

    private static string Normalize(string value) => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static TaxonomyDiscoveryExecution Failure(string error)
    {
        using var document = JsonDocument.Parse($"{{\"classification\":{{\"type\":\"inconclusive\",\"confidence\":0,\"insufficientEvidence\":true}},\"issues\":[],\"pipelineError\":{JsonSerializer.Serialize(error)}}}");
        return new TaxonomyDiscoveryExecution(document.RootElement.Clone(), AppConstants.TaxonomyDiscoveryModel, error, error);
    }

    private const string SystemPrompt = """
        Role
        You are collecting evidence-backed review cards to discover a future content-audit taxonomy. You are not auditing the page, assigning issue codes, deciding whether it is healthy, or judging facts against the outside world. Return only JSON matching the supplied schema.

        Page map
        Describe the page's visible title, primary purpose, main topics, and reader actions from the page itself. visibleTitle must be the literal H1 or equivalent reader-facing title from pageContent, never documentTitle metadata. Use concise neutral language. A page can be complete, historical, or informational; do not treat age, lack of marketing, or lack of external proof as a defect.

        Review observations
        Create a card only for a directly visible, concrete editorial or experience concern that a page owner could remedy. State what is visible, not an inferred intention or severity. Each card needs exact contiguous evidence copied from pageContent and a specific suggested remediation. Do not use canonical issue names.
        Never create a review observation because a page is old, has an old date, may be outdated, makes a factual/legal/technical claim, lacks external proof, or could offer extra optional detail/contact methods. Those are either healthy content choices or external questions. Do not recommend updating a claim, policy, date, or legal mechanism unless the page itself visibly contradicts it.

        External questions
        Use these only for material assertions that require external evidence to validate, including legal, technical, availability, regulatory, medical, financial, or time-sensitive claims presented as current guidance. The question must be answerable through external research and needs an exact page quote. Do not create an external question for ordinary writing quality or a historical statement clearly presented as history.

        Evidence rules
        Work only from pageContent. Text after [site context] is navigation/footer context and cannot be evidence. Every evidence item must be a literal uninterrupted substring from pageContent: no ellipses, paraphrase, brackets, omissions, or text combined from different locations. Prefer no card when the evidence does not directly support it. Return at most five review observations and five external questions.
        """;

    private const string Schema = """
        {"type":"object","additionalProperties":false,"required":["pageMap","reviewObservations","externalQuestions"],"properties":{"pageMap":{"type":"object","additionalProperties":false,"required":["visibleTitle","primaryPurpose","mainTopics","readerActions"],"properties":{"visibleTitle":{"type":"string"},"primaryPurpose":{"type":"string"},"mainTopics":{"type":"array","items":{"type":"string"}},"readerActions":{"type":"array","items":{"type":"string"}}}},"reviewObservations":{"type":"array","maxItems":5,"items":{"type":"object","additionalProperties":false,"required":["observation","evidence","suggestedRemediation"],"properties":{"observation":{"type":"string"},"evidence":{"type":"array","minItems":1,"items":{"type":"string"}},"suggestedRemediation":{"type":"string"}}}},"externalQuestions":{"type":"array","maxItems":5,"items":{"type":"object","additionalProperties":false,"required":["question","evidence"],"properties":{"question":{"type":"string"},"evidence":{"type":"array","minItems":1,"items":{"type":"string"}}}}}}}
        """;
}

internal sealed record TaxonomyDiscoveryInput(string FinalUrl, string Title, string PageLanguage, string PageContent, string OutputLanguage);
internal sealed record TaxonomyDiscoveryExecution(JsonElement Analysis, string Model, string RawResponse, string? Error);
