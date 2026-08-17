using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VersaCore;

// This file owns the secondary pipeline's contracts and behavior. It deliberately
// does not reuse the primary pipeline's claim ledger, prompt stages, resolver, or
// evidence policies. Program.cs only adapts its result to CLI artifacts.
internal sealed class SecondaryPipeline
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SecondaryPipelineExecution> RunAsync(SecondaryPipelineInput input)
    {
        if (string.IsNullOrWhiteSpace(AppConstants.ApiKey))
            return Failure("OpenAI API key is not configured.");
        if (string.IsNullOrWhiteSpace(AppConstants.TavilyApiKey))
            return Failure("Tavily API key is not configured.");

        try
        {
            // Understanding supplies orientation; the intrinsic auditor receives it together
            // with structural facts from the same immutable snapshot.
            var understandingRaw = await RequestUnderstandingAsync(input);
            var understanding = ParseRequiredJson(understandingRaw, "Understanding");
            var intrinsicRaw = await RequestIntrinsicAuditAsync(input, understanding);
            var intrinsic = ParseRequiredJson(intrinsicRaw, "Intrinsic audit");
            var temporalAnchors = ReadTemporalAnchors(understanding);
            var actions = ReadActionCandidates(understanding);
            var claims = ReadClaims(understanding);
            var localAnchorResolutions = ResolveDeclaredPeriods(temporalAnchors, input.AnalysisDate, input.ContentDate);
            var externalActions = actions.Where(action => RequiresExternalResolution(action, temporalAnchors, localAnchorResolutions)).ToArray();
            var actionClaims = externalActions.Select(ToAnchorClaim).ToArray();
            var verification = new List<SecondaryVerificationResult>();
            var retrievalTrace = new List<SecondaryRetrievalTrace>();
            var sourceSelections = new List<string>();

            // Historical records without reader actions do not resolve anchors externally.
            var historicalRecord = understanding.TryGetProperty("historicalRecord", out var historical)
                && historical.ValueKind == JsonValueKind.True;
            var externalClaims = actionClaims.Concat(claims).ToArray();
            if ((!historicalRecord || claims.Count > 0) && externalClaims.Length > 0)
            {
                foreach (var batch in externalClaims.Chunk(3))
                {
                    var retrieval = await SearchEvidenceCandidatesAsync(batch, input);
                    retrievalTrace.AddRange(retrieval.Trace);
                    var selectionRaw = await RequestEvidenceSourceSelectionAsync(batch, retrieval.Sources);
                    sourceSelections.Add(selectionRaw);
                    var selected = ResolveSelectedSources(batch, selectionRaw, retrieval.Sources, retrievalTrace);
                    var extracted = await ExtractEvidenceAsync(batch, selected);
                    retrievalTrace.AddRange(extracted.Trace);
                    var factRaw = await RequestEvidenceEvaluationAsync(input, batch, extracted.Sources);
                    verification.AddRange(ResolveVerificationResults(input, batch, factRaw, extracted.Sources));
                }
            }

            var result = ResolveResult(
                input, understanding, intrinsic, temporalAnchors, localAnchorResolutions,
                actions, claims, verification, retrievalTrace, historicalRecord);

            return new SecondaryPipelineExecution(
                result,
                AppConstants.DiagnosisModel,
                JsonSerializer.Serialize(new { understanding = understandingRaw, intrinsic = intrinsicRaw, sourceSelections, verification }, JsonOptions),
                verification.SelectMany(item => item.SourceUrls).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                null);
        }
        catch (Exception exception)
        {
            return Failure(exception.ToString());
        }
    }

    private static SecondaryPipelineExecution Failure(string error)
    {
        var node = new JsonObject
        {
            ["classification"] = new JsonObject
            {
                ["type"] = "inconclusive",
                ["confidence"] = 0,
                ["insufficientEvidence"] = true
            },
            ["issues"] = new JsonArray(),
            ["claimsToVerify"] = new JsonArray(),
            ["verification"] = new JsonObject { ["claims"] = new JsonArray() },
            ["pipelineError"] = error
        };
        using var document = JsonDocument.Parse(node.ToJsonString(JsonOptions));
        return new SecondaryPipelineExecution(document.RootElement.Clone(), AppConstants.DiagnosisModel, error, [], error);
    }

    private static async Task<string> RequestUnderstandingAsync(SecondaryPipelineInput input)
    {
        var payload = new
        {
            model = AppConstants.IntentModel,
            temperature = 0,
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "page_understanding",
                    strict = true,
                    schema = JsonNode.Parse(UnderstandingSchemaJson)
                }
            },
            messages = new object[]
            {
                new { role = "system", content = UnderstandingSystemPrompt },
                new { role = "user", content = BuildUnderstandingPrompt(input) }
            }
        };
        var raw = await SendOpenAiAsync("chat/completions", payload);
        return ExtractChatCompletionText(raw);
    }

    private static async Task<string> RequestIntrinsicAuditAsync(SecondaryPipelineInput input, JsonElement understanding)
    {
        var payload = new
        {
            model = AppConstants.DiagnosisModel,
            temperature = 0,
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "intrinsic_audit",
                    strict = true,
                    schema = JsonNode.Parse(IntrinsicAuditSchemaJson)
                }
            },
            messages = new object[]
            {
                new { role = "system", content = IntrinsicAuditSystemPrompt },
                new { role = "user", content = BuildIntrinsicAuditPrompt(input, understanding) }
            }
        };
        var raw = await SendOpenAiAsync("chat/completions", payload);
        return ExtractChatCompletionText(raw);
    }

    private static async Task<string> RequestEvidenceEvaluationAsync(
        SecondaryPipelineInput input,
        IReadOnlyList<SecondaryClaimToVerify> claims,
        IReadOnlyList<SecondaryEvidenceSource> evidence)
    {
        var payload = new
        {
            model = AppConstants.FactCheckModel,
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "tavily_evidence_evaluation",
                    strict = true,
                    schema = JsonNode.Parse(EvidenceEvaluationSchemaJson)
                }
            },
            // No web_search tool: this stage can assess only the Tavily packet below.
            input = new object[]
            {
                new { role = "system", content = EvidenceEvaluationSystemPrompt },
                new { role = "user", content = BuildEvidenceEvaluationPrompt(input, claims, evidence) }
            }
        };
        var raw = await SendOpenAiAsync("responses", payload);
        return ExtractResponsesText(raw);
    }

    private static async Task<string> SendOpenAiAsync(string relativeUrl, object payload)
    {
        using var client = new HttpClient { BaseAddress = new Uri(AppConstants.BaseUrl), Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AppConstants.ApiKey);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, relativeUrl)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
                };
                using var response = await client.SendAsync(request);
                var raw = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode) return raw;
                throw new InvalidOperationException($"OpenAI {relativeUrl} returned HTTP {(int)response.StatusCode}: {raw}");
            }
            catch (Exception) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt));
            }
        }
        throw new InvalidOperationException($"OpenAI {relativeUrl} failed after retries.");
    }

    private static async Task<SecondaryEvidenceRetrieval> SearchEvidenceCandidatesAsync(
        IReadOnlyList<SecondaryClaimToVerify> claims,
        SecondaryPipelineInput input)
    {
        using var client = new HttpClient { BaseAddress = new Uri(AppConstants.TavilyBaseUrl), Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AppConstants.TavilyApiKey);
        var sources = new List<SecondaryEvidenceSource>();
        var trace = new List<SecondaryRetrievalTrace>();

        var searchTasks = claims.Select(claim => SearchEvidenceCandidatesForClaimAsync(client, claim, input));
        var searchResults = await Task.WhenAll(searchTasks);
        foreach (var searchResult in searchResults)
        {
            sources.AddRange(searchResult.Sources);
            trace.AddRange(searchResult.Trace);
        }

        return new SecondaryEvidenceRetrieval(
            sources
            .GroupBy(source => $"{source.ClaimId}\u001f{source.Url}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray(),
            trace);
    }

    private static async Task<SecondaryEvidenceRetrieval> SearchEvidenceCandidatesForClaimAsync(
        HttpClient client,
        SecondaryClaimToVerify claim,
        SecondaryPipelineInput input)
    {
        var auditedHost = new Uri(input.FinalUrl).Host;
        var payload = new
        {
            query = claim.ResearchQuestion,
            search_depth = "advanced",
            chunks_per_source = 3,
            max_results = 5,
            include_raw_content = false,
            include_answer = false,
            auto_parameters = true,
            exclude_domains = new[] { auditedHost }
        };
        using var response = await client.PostAsync("search", new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"));
        var raw = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Tavily search for {claim.Id} returned HTTP {(int)response.StatusCode}: {raw}");

        using var document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return new SecondaryEvidenceRetrieval([], []);

        var sources = new List<SecondaryEvidenceSource>();
        var trace = new List<SecondaryRetrievalTrace>();
        foreach (var item in results.EnumerateArray())
        {
            var url = ReadString(item, "url");
            var title = ReadString(item, "title") ?? url ?? string.Empty;
            var content = ReadString(item, "content");
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(content)) continue;
            var score = item.TryGetProperty("score", out var scoreNode) && scoreNode.TryGetDouble(out var value) ? value : 0;
            if (score < 0.5)
            {
                trace.Add(new SecondaryRetrievalTrace(claim.Id, claim.ResearchQuestion, url, title, "excluded", "low_relevance_score"));
                continue;
            }
            trace.Add(new SecondaryRetrievalTrace(claim.Id, claim.ResearchQuestion, url, title, "candidate", $"score={score:F2}"));
            sources.Add(new SecondaryEvidenceSource(claim.Id, url, title, NormalizeWhitespace(content), "search-snippet", "unknown"));
        }
        return new SecondaryEvidenceRetrieval(sources, trace);
    }

    private static async Task<string> RequestEvidenceSourceSelectionAsync(
        IReadOnlyList<SecondaryClaimToVerify> claims,
        IReadOnlyList<SecondaryEvidenceSource> candidates)
    {
        var payload = new
        {
            model = AppConstants.FactCheckModel,
            temperature = 0,
            response_format = new
            {
                type = "json_schema",
                json_schema = new { name = "tavily_source_selection", strict = true, schema = JsonNode.Parse(EvidenceSourceSelectionSchemaJson) }
            },
            messages = new object[]
            {
                new { role = "system", content = EvidenceSourceSelectionSystemPrompt },
                new { role = "user", content = BuildEvidenceSourceSelectionPrompt(claims, candidates) }
            }
        };
        var raw = await SendOpenAiAsync("chat/completions", payload);
        return ExtractChatCompletionText(raw);
    }

    private static IReadOnlyList<SecondaryEvidenceSource> ResolveSelectedSources(
        IReadOnlyList<SecondaryClaimToVerify> claims,
        string selectionRaw,
        IReadOnlyList<SecondaryEvidenceSource> candidates,
        ICollection<SecondaryRetrievalTrace> trace)
    {
        var parsed = ParseRequiredJson(selectionRaw, "Evidence source selection");
        var selectedUrls = parsed.TryGetProperty("selections", out var selections) && selections.ValueKind == JsonValueKind.Array
            ? selections.EnumerateArray().GroupBy(item => ReadString(item, "claimId") ?? string.Empty, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.SelectMany(item => item.GetProperty("candidates").EnumerateArray())
                        .ToDictionary(item => ReadString(item, "url") ?? string.Empty, item => ReadString(item, "sourceClass") ?? "unknown_or_ineligible", StringComparer.OrdinalIgnoreCase),
                    StringComparer.Ordinal)
            : new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var selected = new List<SecondaryEvidenceSource>();
        foreach (var claim in claims)
        {
            var urls = selectedUrls.GetValueOrDefault(claim.Id) ?? [];
            foreach (var candidate in candidates.Where(item => item.ClaimId == claim.Id))
            {
                var disposition = urls.ContainsKey(candidate.Url) ? "selected_for_extract" : "rejected_for_extract";
                trace.Add(new SecondaryRetrievalTrace(claim.Id, claim.ResearchQuestion, candidate.Url, candidate.Title, disposition, urls.GetValueOrDefault(candidate.Url)));
                if (urls.TryGetValue(candidate.Url, out var sourceClass)) selected.Add(candidate with { SourceClass = sourceClass });
            }
        }
        return selected;
    }

    private static async Task<SecondaryEvidenceRetrieval> ExtractEvidenceAsync(
        IReadOnlyList<SecondaryClaimToVerify> claims,
        IReadOnlyList<SecondaryEvidenceSource> selected)
    {
        using var client = new HttpClient { BaseAddress = new Uri(AppConstants.TavilyBaseUrl), Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AppConstants.TavilyApiKey);
        var claimsById = claims.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var tasks = selected.GroupBy(item => item.ClaimId).Select(group => ExtractEvidenceForClaimAsync(client, claimsById[group.Key], group.ToArray()));
        var extracted = await Task.WhenAll(tasks);
        return new SecondaryEvidenceRetrieval(extracted.SelectMany(item => item.Sources).ToArray(), extracted.SelectMany(item => item.Trace).ToArray());
    }

    private static async Task<SecondaryEvidenceRetrieval> ExtractEvidenceForClaimAsync(
        HttpClient client,
        SecondaryClaimToVerify claim,
        IReadOnlyList<SecondaryEvidenceSource> selected)
    {
        if (selected.Count == 0) return new SecondaryEvidenceRetrieval([], []);
        var claimId = claim.Id;
        var payload = new { urls = selected.Select(item => item.Url).ToArray(), query = claim.ResearchQuestion, chunks_per_source = 3, extract_depth = "basic", format = "markdown" };
        using var response = await client.PostAsync("extract", new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"));
        var raw = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Tavily extract for {claimId} returned HTTP {(int)response.StatusCode}: {raw}");
        using var document = JsonDocument.Parse(raw);
        var results = document.RootElement.TryGetProperty("results", out var values) && values.ValueKind == JsonValueKind.Array ? values : default;
        var sources = new List<SecondaryEvidenceSource>();
        var trace = new List<SecondaryRetrievalTrace>();
        if (results.ValueKind == JsonValueKind.Array)
            foreach (var item in results.EnumerateArray())
            {
                var url = ReadString(item, "url");
                var content = ReadString(item, "raw_content");
                var original = selected.FirstOrDefault(source => string.Equals(source.Url, url, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(content) || original is null) continue;
                var chunks = content.Split("[...]", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                for (var index = 0; index < chunks.Length; index++)
                {
                    var chunk = NormalizeWhitespace(chunks[index]);
                    if (chunk.Length == 0) continue;
                    var chunkId = $"chunk-{index + 1}";
                    trace.Add(new SecondaryRetrievalTrace(claimId, claim.ResearchQuestion, url, original.Title, "extracted", chunkId));
                    sources.Add(new SecondaryEvidenceSource(claimId, url, original.Title, chunk, chunkId, original.SourceClass));
                }
            }
        return new SecondaryEvidenceRetrieval(sources, trace);
    }

    private static IReadOnlyList<SecondaryClaimToVerify> ReadClaims(JsonElement understanding)
    {
        if (!understanding.TryGetProperty("claimsToVerify", out var rawClaims) || rawClaims.ValueKind != JsonValueKind.Array)
            return [];
        var claims = new List<SecondaryClaimToVerify>();
        foreach (var item in rawClaims.EnumerateArray())
        {
            var quote = NormalizeWhitespace(ReadString(item, "pageQuote"));
            claims.Add(new SecondaryClaimToVerify(
                ReadString(item, "id") ?? $"claim-{claims.Count + 1}",
                ReadString(item, "statement") ?? string.Empty,
                quote,
                ReadString(item, "whyItMatters") ?? string.Empty,
                ReadString(item, "verificationNeed") ?? "current_status",
                ReadString(item, "authorityDomain") ?? "other",
                ReadString(item, "researchQuestion") ?? string.Empty));
        }
        return claims;
    }

    private static IReadOnlyList<SecondaryActionCandidate> ReadActionCandidates(JsonElement understanding)
    {
        if (!understanding.TryGetProperty("actionCandidates", out var rawActions) || rawActions.ValueKind != JsonValueKind.Array)
            return [];

        var actions = new List<SecondaryActionCandidate>();
        foreach (var item in rawActions.EnumerateArray())
        {
            actions.Add(new SecondaryActionCandidate(
                ReadString(item, "id") ?? $"action-{actions.Count + 1}",
                ReadString(item, "action") ?? string.Empty,
                ReadString(item, "actionType") ?? "other",
                ReadString(item, "entity") ?? string.Empty,
                ReadString(item, "anchor") ?? string.Empty,
                ReadString(item, "boundAnchorId"),
                NormalizeWhitespace(ReadString(item, "pageQuote"))));
        }
        return actions;
    }

    private static IReadOnlyList<SecondaryTemporalAnchor> ReadTemporalAnchors(JsonElement understanding)
    {
        if (!understanding.TryGetProperty("temporalAnchors", out var rawAnchors) || rawAnchors.ValueKind != JsonValueKind.Array)
            return [];

        var anchors = new List<SecondaryTemporalAnchor>();
        foreach (var item in rawAnchors.EnumerateArray())
        {
            anchors.Add(new SecondaryTemporalAnchor(
                ReadString(item, "id") ?? $"anchor-{anchors.Count + 1}",
                ReadString(item, "kind") ?? "deadline",
                ReadString(item, "entity") ?? string.Empty,
                ReadString(item, "aspect") ?? string.Empty,
                ReadJsonInt(item, "periodYear"),
                ReadJsonInt(item, "periodMonth"),
                ReadJsonInt(item, "periodDay"),
                ReadJsonBool(item, "governsReaderAction"),
                ReadString(item, "governedActionId"),
                ReadString(item, "relation") ?? "other",
                ReadString(item, "pageQuote") ?? string.Empty));
        }
        return anchors;
    }

    private static IReadOnlyList<SecondaryAnchorResolution> ResolveDeclaredPeriods(
        IReadOnlyList<SecondaryTemporalAnchor> anchors,
        DateOnly capturedAt,
        string contentDate)
    {
        return anchors.Select(anchor =>
        {
            if (!anchor.GovernsReaderAction)
                return new SecondaryAnchorResolution(anchor.Id, "not_action_governing", null, "The date is editorial or descriptive metadata and does not govern a reader action.");
            if (IsPublicationDateAnchor(anchor, contentDate))
                return new SecondaryAnchorResolution(anchor.Id, "needs_external_resolution", null, "The declared period matches the page publication or update date.");
            if (anchor.PeriodYear is not { } year)
                return new SecondaryAnchorResolution(anchor.Id, "needs_external_resolution", null, "No explicit end period was declared.");

            var month = anchor.PeriodMonth ?? 12;
            var day = anchor.PeriodDay ?? DateTime.DaysInMonth(year, month);
            var endDate = new DateOnly(year, month, day);
            return new SecondaryAnchorResolution(
                anchor.Id,
                endDate < capturedAt ? "resolved_expired" : "resolved_active",
                endDate,
                "Compared the explicit declared period with the snapshot capture date.");
        }).ToArray();
    }

    private static bool IsPublicationDateAnchor(SecondaryTemporalAnchor anchor, string contentDate)
    {
        if (!DateTimeOffset.TryParse(contentDate, out var metadataDate)
            || anchor.PeriodYear != metadataDate.Year
            || anchor.PeriodMonth != metadataDate.Month
            || anchor.PeriodDay != metadataDate.Day)
            return false;

        return anchor.PageQuote.Trim().Length <= 40;
    }

    private static bool RequiresExternalResolution(
        SecondaryActionCandidate action,
        IReadOnlyList<SecondaryTemporalAnchor> anchors,
        IReadOnlyList<SecondaryAnchorResolution> resolutions) =>
        action.BoundAnchorId is not null
        && IsActionGovernedByAnchor(action, anchors)
        && resolutions.FirstOrDefault(item => item.AnchorId == action.BoundAnchorId)?.Status == "needs_external_resolution";

    private static bool IsActionGovernedByAnchor(
        SecondaryActionCandidate action,
        IReadOnlyList<SecondaryTemporalAnchor> anchors) =>
        action.BoundAnchorId is not null
        && anchors.Any(anchor =>
            anchor.Id == action.BoundAnchorId
            && anchor.GovernsReaderAction
            && string.Equals(anchor.GovernedActionId, action.Id, StringComparison.Ordinal));

    private static SecondaryClaimToVerify ToAnchorClaim(SecondaryActionCandidate action) => new(
        action.Id,
        $"{action.Action} for {action.Entity} is currently available.",
        action.PageQuote,
        $"A reader may act on this {action.ActionType}.",
        "current_status",
        "other",
        $"What is the current official status of {action.Action} for {action.Entity}?");

    private static IReadOnlyList<SecondaryVerificationResult> ResolveVerificationResults(
        SecondaryPipelineInput input,
        IReadOnlyList<SecondaryClaimToVerify> claims,
        string factRaw,
        IReadOnlyList<SecondaryEvidenceSource> evidence)
    {
        var parsed = ParseRequiredJson(factRaw, "Evidence evaluation");
        var returned = parsed.TryGetProperty("checks", out var checks) && checks.ValueKind == JsonValueKind.Array
            ? checks.EnumerateArray()
                .GroupBy(item => ReadString(item, "claimId") ?? string.Empty, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var results = new List<SecondaryVerificationResult>();

        foreach (var claim in claims)
        {
            var consultedUrls = evidence.Where(item => item.ClaimId == claim.Id).Select(item => item.Url).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (!returned.TryGetValue(claim.Id, out var check))
            {
                results.Add(SecondaryVerificationResult.NotEnoughEvidence(claim, "The evidence evaluator did not return a result for this claim.", consultedUrls));
                continue;
            }
            var citedEvidence = ReadCitedEvidence(check, claim, evidence);
            if (citedEvidence.Count == 0 && ReadString(check, "verdict") != "not_enough_evidence")
            {
                results.Add(SecondaryVerificationResult.NotEnoughEvidence(
                    claim,
                    "The evaluator did not provide a literal evidence quote inside an extracted Tavily chunk.", consultedUrls));
                continue;
            }
            var hasSourceOfRecord = citedEvidence.Any(item => item.SourceClass == "source_of_record");
            var independentSecondaryCount = citedEvidence.Where(item => item.SourceClass == "independent_secondary")
                .Select(item => new Uri(item.Url).Host).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var evidenceIsSufficient = claim.VerificationNeed == "current_status"
                ? hasSourceOfRecord
                : hasSourceOfRecord || independentSecondaryCount >= 2;
            if (ReadString(check, "verdict") != "not_enough_evidence" && !evidenceIsSufficient)
            {
                results.Add(SecondaryVerificationResult.NotEnoughEvidence(
                    claim,
                    claim.VerificationNeed == "current_status"
                        ? "A current-status claim requires a source of record."
                        : "A historical claim requires a source of record or two independent secondary sources.", consultedUrls));
                continue;
            }
            if (citedEvidence.Any(item => IsAuditedPageOrVariant(item.Url, input)))
            {
                results.Add(SecondaryVerificationResult.NotEnoughEvidence(
                    claim,
                    "A historical copy, canonical variant, or AMP variant of the audited page cannot prove current status.", consultedUrls));
                continue;
            }
            var selectedEvidence = citedEvidence.FirstOrDefault(item => item.SourceClass == "source_of_record") ?? citedEvidence.FirstOrDefault();
            results.Add(new SecondaryVerificationResult(
                claim,
                ReadString(check, "verdict") ?? "not_enough_evidence",
                ReadString(check, "reason") ?? string.Empty,
                ReadString(check, "currentFact"),
                selectedEvidence?.Url,
                selectedEvidence?.Quote,
                citedEvidence.Count == 0 ? consultedUrls : citedEvidence.Select(item => item.Url).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                selectedEvidence?.SourceClass == "source_of_record" ? "primary_authority" : selectedEvidence?.SourceClass == "independent_secondary" ? "secondary" : "unknown",
                evidenceIsSufficient ? "accepted" : "rejected"));
        }
        return results;
    }

    private static IReadOnlyList<SecondaryCitedEvidence> ReadCitedEvidence(
        JsonElement check,
        SecondaryClaimToVerify claim,
        IReadOnlyList<SecondaryEvidenceSource> packet)
    {
        if (!check.TryGetProperty("evidence", out var rawEvidence) || rawEvidence.ValueKind != JsonValueKind.Array) return [];
        var result = new List<SecondaryCitedEvidence>();
        foreach (var item in rawEvidence.EnumerateArray())
        {
            var url = ReadString(item, "sourceUrl");
            var chunkId = ReadString(item, "chunkId");
            var quote = NormalizeWhitespace(ReadString(item, "quote"));
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(chunkId) || quote.Length < 20) continue;
            var source = packet.FirstOrDefault(candidate => candidate.ClaimId == claim.Id
                && string.Equals(candidate.Url, url, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.ChunkId, chunkId, StringComparison.Ordinal));
            if (source is null || !source.Content.Contains(quote, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(new SecondaryCitedEvidence(source.Url, source.ChunkId, quote, source.SourceClass));
        }
        return result;
    }

    private static JsonElement ResolveResult(
        SecondaryPipelineInput input,
        JsonElement understanding,
        JsonElement intrinsic,
        IReadOnlyList<SecondaryTemporalAnchor> temporalAnchors,
        IReadOnlyList<SecondaryAnchorResolution> anchorResolutions,
        IReadOnlyList<SecondaryActionCandidate> actions,
        IReadOnlyList<SecondaryClaimToVerify> claims,
        IReadOnlyList<SecondaryVerificationResult> verification,
        IReadOnlyList<SecondaryRetrievalTrace> retrievalTrace,
        bool historicalRecord)
    {
        var root = JsonNode.Parse(understanding.GetRawText())!.AsObject();
        var issues = JsonNode.Parse(intrinsic.GetRawText())?["issues"]?.AsArray().DeepClone().AsArray() ?? new JsonArray();
        foreach (var issue in issues.OfType<JsonObject>()) issue["source"] = "page";
        var actionIds = actions.Select(action => action.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var resolution in anchorResolutions.Where(item => item.Status == "resolved_expired"))
        {
            var boundActions = actions.Where(action =>
                action.BoundAnchorId == resolution.AnchorId
                && IsActionGovernedByAnchor(action, temporalAnchors)).ToArray();
            if (boundActions.Length == 0) continue;
            var anchor = temporalAnchors.FirstOrDefault(item => item.Id == resolution.AnchorId);
            var quotes = new JsonArray();
            if (!string.IsNullOrWhiteSpace(anchor?.PageQuote)) quotes.Add(anchor.PageQuote);
            foreach (var action in boundActions)
                if (!quotes.Any(node => node?.GetValue<string>() == action.PageQuote)) quotes.Add(action.PageQuote);
            issues.Add(new JsonObject
            {
                ["code"] = "expired_date_or_deadline",
                ["source"] = "declared_period",
                ["anchorId"] = resolution.AnchorId,
                ["reason"] = resolution.Reason,
                ["pageQuotes"] = quotes,
                ["resolvedEndDate"] = resolution.ResolvedEndDate?.ToString("yyyy-MM-dd")
            });
        }

        foreach (var group in actions.Where(action => action.BoundAnchorId is null && IsStaleRisk(input.ContentDate))
                     .GroupBy(action => $"{action.ActionType}\u001f{action.Entity}", StringComparer.OrdinalIgnoreCase))
        {
            var action = group.First();
            issues.Add(new JsonObject
            {
                ["code"] = "stale_risk",
                ["source"] = "freshness_policy",
                ["actionId"] = action.Id,
                ["reason"] = "The page presents an old central reader action without a declared operational end period.",
                ["pageQuotes"] = new JsonArray(group.Select(item => (JsonNode?)item.PageQuote).Distinct().ToArray())
            });
        }

        foreach (var result in verification.Where(item => actionIds.Contains(item.Claim.Id)))
        {
            if (result.Verdict is "contradicted" or "outdated")
            {
                issues.Add(new JsonObject
                {
                    ["code"] = "expired_date_or_deadline",
                    ["source"] = "anchor_resolution",
                    ["actionId"] = result.Claim.Id,
                    ["reason"] = result.Reason,
                    ["pageQuotes"] = new JsonArray(result.Claim.PageQuote),
                    ["currentFact"] = result.CurrentFact,
                    ["evidence"] = new JsonObject { ["sourceUrl"] = result.SourceUrl, ["quote"] = result.EvidenceQuote }
                });
                continue;
            }
            if (result.Verdict == "not_enough_evidence" && IsStaleRisk(input.ContentDate))
            {
                issues.Add(new JsonObject
                {
                    ["code"] = "stale_risk",
                    ["source"] = "anchor_resolution",
                    ["actionId"] = result.Claim.Id,
                    ["reason"] = "The page makes an old operational promise whose current status could not be resolved from consulted sources.",
                    ["pageQuotes"] = new JsonArray(result.Claim.PageQuote),
                    ["sourcesConsulted"] = new JsonArray(result.SourceUrls.Select(url => (JsonNode?)url).ToArray())
                });
            }
        }

        foreach (var result in verification.Where(item => !actionIds.Contains(item.Claim.Id) && item.Verdict is "contradicted" or "outdated"))
        {
            issues.Add(new JsonObject
            {
                ["code"] = "verified_current_claim_issue",
                ["source"] = "tavily_fact_check",
                ["claimId"] = result.Claim.Id,
                ["reason"] = result.Reason,
                ["pageQuotes"] = new JsonArray(result.Claim.PageQuote),
                ["currentFact"] = result.CurrentFact,
                ["evidence"] = new JsonObject { ["sourceUrl"] = result.SourceUrl, ["quote"] = result.EvidenceQuote }
            });
        }

        root["issues"] = issues;
        root["claimsToVerify"] = new JsonArray(claims.Select(claim => JsonSerializer.SerializeToNode(claim, JsonOptions)).ToArray());
        root["actionCandidates"] = new JsonArray(actions.Select(action => JsonSerializer.SerializeToNode(action, JsonOptions)).ToArray());
        root["temporalAnchors"] = new JsonArray(temporalAnchors.Select(anchor => JsonSerializer.SerializeToNode(anchor, JsonOptions)).ToArray());
        root["anchorResolutions"] = new JsonArray(anchorResolutions.Select(resolution => JsonSerializer.SerializeToNode(resolution, JsonOptions)).ToArray());
        root["temporalPlanSkipped"] = historicalRecord && actions.Count == 0;
        root["verification"] = new JsonObject
        {
            ["claims"] = new JsonArray(verification.Select(item => JsonSerializer.SerializeToNode(item, JsonOptions)).ToArray()),
            ["retrieval"] = new JsonArray(retrievalTrace.Select(item => JsonSerializer.SerializeToNode(item, JsonOptions)).ToArray()),
            ["summary"] = new JsonObject
            {
                ["submitted"] = verification.Count,
                ["validated"] = verification.Count(item => item.SourceUrl is not null),
                ["notEnoughEvidence"] = verification.Count(item => item.Verdict == "not_enough_evidence")
            }
        };
        root["classification"] = new JsonObject
        {
            ["type"] = issues.Count > 0 ? "unhealthy" : "healthy",
            ["confidence"] = issues.Count > 0 ? 0.95 : 0.8, //makes no sense
            ["insufficientEvidence"] = verification.Any(item => item.Verdict == "not_enough_evidence")
        };

        using var document = JsonDocument.Parse(root.ToJsonString(JsonOptions));
        return document.RootElement.Clone();
    }

    private static bool IsStaleRisk(string contentDate) =>
        DateTimeOffset.TryParse(contentDate, out var date)
        && date < DateTimeOffset.UtcNow.AddDays(-365);

    private static string BuildUnderstandingPrompt(SecondaryPipelineInput input) => $"""
        outputLanguage: {input.OutputLanguage}
        url: {input.Url}
        finalUrl: {input.FinalUrl}
        title: {input.Title}
        pageLanguage: {input.PageLanguage}

        pageContent:
        {input.PageContent}
        """;

    private static string BuildIntrinsicAuditPrompt(SecondaryPipelineInput input, JsonElement understanding) => $"""
        outputLanguage: {input.OutputLanguage}
        url: {input.Url}
        finalUrl: {input.FinalUrl}
        title: {input.Title}
        pageLanguage: {input.PageLanguage}
        pageIntent: {ReadString(understanding, "intent") ?? "unknown"}
        purpose: {ReadString(understanding, "pageSummary") ?? string.Empty}

        structuralInventory (derived from the same visible snapshot; it is evidence, not a verdict):
        {JsonSerializer.Serialize(input.StructuralInventory, JsonOptions)}

        pageContent:
        {input.PageContent}
        """;

    private static string BuildEvidenceEvaluationPrompt(
        SecondaryPipelineInput input,
        IReadOnlyList<SecondaryClaimToVerify> claims,
        IReadOnlyList<SecondaryEvidenceSource> evidence)
    {
        var builder = new StringBuilder($"analysisDate: {input.AnalysisDate:yyyy-MM-dd}\nauditedUrl: {input.FinalUrl}\nclaims:\n");
        foreach (var claim in claims)
        {
            builder.AppendLine($"- id: {claim.Id}");
            builder.AppendLine($"  statement: {claim.Statement}");
            builder.AppendLine($"  pageQuote: {claim.PageQuote}");
            builder.AppendLine($"  verificationNeed: {claim.VerificationNeed}");
            builder.AppendLine($"  authorityDomain: {claim.AuthorityDomain}");
            builder.AppendLine($"  researchQuestion: {claim.ResearchQuestion}");
        }
        builder.AppendLine("Tavily evidence packet:");
        foreach (var source in evidence)
        {
            builder.AppendLine($"- claimId: {source.ClaimId}");
            builder.AppendLine($"- url: {source.Url}");
            builder.AppendLine($"  chunkId: {source.ChunkId}");
            builder.AppendLine($"  sourceClass: {source.SourceClass}");
            builder.AppendLine($"  title: {source.Title}");
            builder.AppendLine($"  content: {Truncate(source.Content, 6000)}");
        }
        return builder.ToString();
    }

    private static string BuildEvidenceSourceSelectionPrompt(
        IReadOnlyList<SecondaryClaimToVerify> claims,
        IReadOnlyList<SecondaryEvidenceSource> candidates)
    {
        var builder = new StringBuilder("claims:\n");
        foreach (var claim in claims)
        {
            builder.AppendLine($"- id: {claim.Id}");
            builder.AppendLine($"  authorityDomain: {claim.AuthorityDomain}");
            builder.AppendLine($"  researchQuestion: {claim.ResearchQuestion}");
        }
        builder.AppendLine("Tavily search candidates:");
        foreach (var candidate in candidates)
        {
            builder.AppendLine($"- claimId: {candidate.ClaimId}");
            builder.AppendLine($"  url: {candidate.Url}");
            builder.AppendLine($"  title: {candidate.Title}");
            builder.AppendLine($"  snippet: {candidate.Content}");
        }
        return builder.ToString();
    }

    private static string ExtractChatCompletionText(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
            ?? throw new InvalidOperationException("Understanding response did not contain text.");
    }

    private static string ExtractResponsesText(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        foreach (var item in document.RootElement.GetProperty("output").EnumerateArray())
        {
            if (ReadString(item, "type") != "message" || !item.TryGetProperty("content", out var content)) continue;
            foreach (var part in content.EnumerateArray())
                if (ReadString(part, "type") == "output_text") return ReadString(part, "text") ?? string.Empty;
        }
        throw new InvalidOperationException("Evidence evaluation response did not contain text.");
    }

    private static JsonElement ParseRequiredJson(string text, string stage)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"{stage} did not return valid JSON.", exception);
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadJsonInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool ReadJsonBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string NormalizeWhitespace(string? value) =>
        string.Join(" ", (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];


    private static string AuthorityHint(string authorityDomain) => authorityDomain switch
    {
        "government" => "government regulator official",
        "regulator" => "regulator official",
        "vendor" => "vendor official documentation",
        "organizer" => "organizer official",
        "company" => "company official",
        "data_owner" => "dataset owner original report",
        _ => "primary source"
    };

    private static bool IsAuditedPageOrVariant(string? sourceUrl, SecondaryPipelineInput input)
    {
        var source = NormalizeComparableUrl(sourceUrl);
        var original = NormalizeComparableUrl(input.Url);
        var final = NormalizeComparableUrl(input.FinalUrl);
        if (source is null || original is null || final is null) return false;
        return source == original || source == final
            || RemoveAmpSuffix(source) == RemoveAmpSuffix(original)
            || RemoveAmpSuffix(source) == RemoveAmpSuffix(final);
    }

    private static string? NormalizeComparableUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        var path = uri.AbsolutePath.TrimEnd('/').ToLowerInvariant();
        return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}{path}";
    }

    private static string RemoveAmpSuffix(string url) =>
        url.EndsWith("/amp", StringComparison.Ordinal) ? url[..^4] : url;

    private const string UnderstandingSystemPrompt = """
        You are a senior web content auditor. Read the page and return its purpose, intent, summary, historical framing, reader action candidates, and claims requiring external verification. Do not diagnose issues or decide whether an action is active or expired.

        Fill every field according to its contract:
        - purpose: the dominant promise made to a current visitor. type is a concise stable label; rationale cites visible page evidence.
        - pageSummary: a factual, neutral summary of the main content and audience; never include a health verdict.
        - intent: the dominant role or function of the page for a current visitor. Choose exactly one:
        - article: explanatory, editorial, educational, legal, tax, immigration, or practical guidance consumed directly on the page
        - referral_stub: short bridge page whose main purpose is to send the user to one clear destination, asset, or next step where the real substance exists; this includes thin article-like teaser pages that show a title, byline or date, a short intro, and a featured link or CTA but do not deliver meaningful standalone editorial depth in the page body
        - hub_index: listing or index page grouping multiple items, links, resources, articles, services, locations, or categories whose detail lives elsewhere
        - pricing: page primarily about plans, packages, rates, pricing tiers, billing, subscriptions, or fees
        - product: page primarily presenting one concrete product, service, offer, platform, solution, or feature in depth
        - contact: page whose dominant user task is contacting the organization, finding contact details, submitting a contact form, or locating offices
        - landing: broad promotional or conversion-focused page for a company, campaign, practice area, audience, market, or high-level offering
        - faq: page mainly organized as questions and answers
        - legal: privacy policy, terms of use, cookie policy, disclaimer, accessibility statement, compliance notice, or similar legal/compliance page
        - support_doc: documentation, setup guide, troubleshooting guide, technical reference, API reference, user manual, or operational help page
        - profile_bio: page focused primarily on one person, professional profile, biography, author, team member, speaker, or leadership profile
        - composite_page: intentionally mixed page where multiple purposes are substantially present and no single role clearly dominates
        - announcement: company, product, or corporate update about a milestone, launch, award, organizational change, release, partnership, or internal change
        - event_coverage: recap, report, summary, or coverage centered on a named event, conference, webinar, ceremony, or session
        - job_posting: recruiting page for one specific role or vacancy
        - unknown: the readable page role is genuinely indeterminable, incoherent, or too incomplete to classify

        - historicalRecord: true only when the page explicitly frames itself as an archive, recap, completed event record, or historical record. An old date alone does not make it historical.
        - temporalAnchors: extract explicit dates or periods that are relevant to the page's temporal meaning. kind is deadline, event_edition, application_window, eligibility_period, or availability_period. periodYear, periodMonth, and periodDay must contain only numbers explicitly stated by the page; use null when absent. Do not infer a date. For every anchor, determine its function, not merely its wording:
          - governsReaderAction=true only when the date directly governs a reader's action, eligibility, availability, obligation, or operational deadline. governedActionId must be the id of the one actionCandidate it governs, and relation must describe that function.
          - governsReaderAction=false when it only describes the page or a past fact: publication, byline, announcement, update, revision, last-updated label, article date, historical reference, or editorial metadata. governedActionId must be null and relation must be editorial_metadata. A sentence can use words such as updated or modified and still govern an action: for example, "the deadline to submit was modified to 10/10/2026" is action-governing because it changes the submission deadline, not because it contains the word modified.
          pageQuote must be exact and contiguous.
        - actionCandidates: extract only reader-directed actions central to the page's main promise that have a state verifiable from an authoritative external source. actionType is registration, application, purchase, booking, eligibility, or procedure. boundAnchorId must be null unless it references an anchor with governsReaderAction=true and governedActionId equal to this action's id. Each needs an exact contiguous pageQuote. Ignore navigation, related-content links, generic contact forms, newsletter forms, search forms, social links, widgets, footer CTAs, and offers unrelated to the page's main promise. Do not extract editorial forecasts, company plans, product roadmaps, or statements about what an organization might do.
        - claimsToVerify: this is a retrieval agenda, not a summary of important statements. Emit a claim only when it is one atomic, externally verifiable factual assertion that an independent authoritative source could directly confirm or contradict. It must be material to a visitor's decision or understanding today.
          researchQuestion is one short, neutral natural-language question for finding the source of record for this claim. Ask what official, original, or first-party source establishes the fact; do not embed the expected answer, repeat the full claim, name a guessed website, or use keyword stuffing.
          Do not emit legal or professional advice; conditional, case-by-case, or analytical guidance; statements depending on an individual's circumstances; vague claims such as "may be possible", "requires analysis", or "depends on factors"; or facts that cannot realistically be verified independently. Prefer claims with a clear authoritative source over broad coverage. Every claim needs an exact contiguous pageQuote. Return at most three claims.

        Return only JSON matching the schema.
        """;

    private const string IntrinsicAuditSystemPrompt = """
        You are a senior web content auditor. Diagnose only intrinsic issues: defects provable from the visible page snapshot alone. Do not use outside knowledge, dates relative to today, or claims requiring verification.

        The default outcome is no issue: return an empty issues array whenever the page substantively fulfills its purpose and no issue definition is directly proven. Do not infer a defect from an alternative editorial preference, a possible ambiguity, or information that would merely be nice to add. Emit an issue only when visible page evidence directly satisfies that issue's definition.
        pageIntent, purpose, and structuralInventory are factual context extracted from that same snapshot. Use them to orient the audit, but do not treat them as a health verdict. In particular, a form with a submit control, a relevant contact method, or a relevant CTA in structuralInventory is a usable next step even when it appears before the final section. Do not infer that a transactional next step is missing solely because the final CTA is text-only.

        For issues, use only this taxonomy:
        - insufficient_information: too little usable main content to assess fulfillment of the page's purpose; not merely missing optional detail or public prices.
        - unclear_messaging: a materially confusing, vague, disorganized, or difficult-to-follow main message; not minor style issues.
        - within_page_inconsistency: two visible material statements contradict each other; not a conflict requiring outside knowledge.
        - title_body_mismatch: title or major heading makes a specific material promise that the main body does not substantively address or directly contradicts. Conditions, exceptions, transitions, or case-by-case limitations do not constitute a mismatch when the body explains them.
        - url_content_mismatch: a semantically specific URL promise is not delivered by main content; not a legacy, broad, or branded URL.
        - missing_trust_context: necessary author, source, jurisdiction, date, method, attribution, or scope is absent and materially limits reliance. Do not emit this issue merely because an informational or legal page lacks external citations when it visibly identifies a qualified author or reviewer and states the relevant jurisdiction or legal basis.
        - missing_expected_next_step: a transactional page has no visible, usable next step for its central action. Evaluate the whole main content, not only the final CTA or the end of the page. A labeled CTA, contact method, booking flow, application form, or form with a submit action anywhere in the central conversion content is a clear next step. Do not require details about what happens after submission or require the control to appear after the final CTA. Never use for informational or historical pages.
        - brand_safety_risk: visible main content creates a material reputational, policy, or advertiser-suitability risk.
        - sensitive_topic_without_context: a sensitive subject lacks material care, context, framing, or scope needed for its purpose.
        - language_mismatch: substantial main reader-facing content conflicts with declared language, locale route, or language version. Ignore navigation, footer, names, short quotes, and widgets.
        - misleading_current_status: pageContent itself proves something ended, replaced, or unavailable while another visible part promotes it as current.

        Do not emit expired_date_or_deadline or any issue requiring external state. Do not extract claims.
        Emit one issue per distinct underlying defect. When multiple page quotes prove the same defect, return one issue with every material proof in pageQuotes; do not create duplicate issues only because there is another supporting quote.
        Every pageQuotes item must be an exact contiguous page substring that directly proves the issue definition.

        Return only JSON matching the schema.
        """;

    private const string EvidenceEvaluationSystemPrompt = """
        Evaluate claims only against the supplied Tavily evidence packet. Do not browse, use outside knowledge, invent a source, or cite a URL absent from the packet.
        Only evaluate packet entries with the same claimId as the claim. Each entry is one independently addressable chunk. Choose quotes wholly inside one chunk; never quote across chunks or include the `[...]` separator.
        Use supported when the selected evidence directly supports the claim, contradicted when it directly conflicts, outdated when it shows a presented current claim is no longer current, and not_enough_evidence otherwise. Return up to two evidence items. The code adjudicates evidence strength; do not use outside knowledge.
        """;

    private const string EvidenceSourceSelectionSystemPrompt = """
        Select up to two Tavily candidates per claim for targeted extraction. Classify each selected candidate as source_of_record (issuing authority, regulator, organiser, manufacturer, company, or original data publisher), independent_secondary (an independent publisher reporting the fact), or unknown_or_ineligible. Prefer source_of_record. Select independent_secondary only for a historical fact when no source of record is available; do not select commentary, law firms, consultancies, resellers, SEO pages, news summaries, or aggregators merely because they agree with the claim. If no candidate plausibly qualifies, return an empty candidates array.
        Return only JSON matching the schema.
        """;

    private const string UnderstandingSchemaJson = """
        {"type":"object","additionalProperties":false,"required":["purpose","pageSummary","intent","historicalRecord","temporalAnchors","actionCandidates","claimsToVerify"],"properties":{
          "purpose":{"type":"object","additionalProperties":false,"required":["type","rationale"],"properties":{"type":{"type":"string"},"rationale":{"type":"string"}}},
          "pageSummary":{"type":"string"},
          "intent":{"type":"string","enum":["article","referral_stub","hub_index","pricing","product","contact","landing","faq","legal","support_doc","profile_bio","composite_page","announcement","event_coverage","job_posting","unknown"]},
          "historicalRecord":{"type":"boolean"},
          "temporalAnchors":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["id","kind","entity","aspect","periodYear","periodMonth","periodDay","governsReaderAction","governedActionId","relation","pageQuote"],"properties":{"id":{"type":"string"},"kind":{"type":"string","enum":["deadline","event_edition","application_window","eligibility_period","availability_period"]},"entity":{"type":"string"},"aspect":{"type":"string"},"periodYear":{"type":["integer","null"]},"periodMonth":{"type":["integer","null"]},"periodDay":{"type":["integer","null"]},"governsReaderAction":{"type":"boolean"},"governedActionId":{"type":["string","null"]},"relation":{"type":"string","enum":["deadline","eligibility","availability","event_schedule","editorial_metadata","other"]},"pageQuote":{"type":"string"}}}},
          "actionCandidates":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["id","action","actionType","entity","anchor","boundAnchorId","pageQuote"],"properties":{"id":{"type":"string"},"action":{"type":"string"},"actionType":{"type":"string","enum":["registration","application","purchase","booking","eligibility","procedure"]},"entity":{"type":"string"},"anchor":{"type":"string"},"boundAnchorId":{"type":["string","null"]},"pageQuote":{"type":"string"}}}},
          "claimsToVerify":{"type":"array","maxItems":3,"items":{"type":"object","additionalProperties":false,"required":["id","statement","pageQuote","whyItMatters","verificationNeed","authorityDomain","researchQuestion"],"properties":{"id":{"type":"string"},"statement":{"type":"string"},"pageQuote":{"type":"string"},"whyItMatters":{"type":"string"},"verificationNeed":{"type":"string","enum":["current_status","historical_fact"]},"authorityDomain":{"type":"string","enum":["government","regulator","vendor","organizer","company","data_owner","other"]},"researchQuestion":{"type":"string"}}}}
        }}
        """;

    private const string IntrinsicAuditSchemaJson = """
        {"type":"object","additionalProperties":false,"required":["issues"],"properties":{
          "issues":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["code","reason","pageQuotes"],"properties":{"code":{"type":"string","enum":["insufficient_information","unclear_messaging","within_page_inconsistency","title_body_mismatch","url_content_mismatch","missing_trust_context","missing_expected_next_step","brand_safety_risk","sensitive_topic_without_context","language_mismatch","misleading_current_status"]},"reason":{"type":"string"},"pageQuotes":{"type":"array","minItems":1,"items":{"type":"string"}}}}}
        }}
        """;

    private const string EvidenceEvaluationSchemaJson = """
        {"type":"object","additionalProperties":false,"required":["checks"],"properties":{"checks":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["claimId","verdict","reason","currentFact","evidence"],"properties":{"claimId":{"type":"string"},"verdict":{"type":"string","enum":["supported","contradicted","outdated","not_enough_evidence"]},"reason":{"type":"string"},"currentFact":{"type":["string","null"]},"evidence":{"type":"array","maxItems":2,"items":{"type":"object","additionalProperties":false,"required":["sourceUrl","chunkId","quote"],"properties":{"sourceUrl":{"type":"string"},"chunkId":{"type":"string"},"quote":{"type":"string"}}}}}}}}}
        """;

    private const string EvidenceSourceSelectionSchemaJson = """
        {"type":"object","additionalProperties":false,"required":["selections"],"properties":{"selections":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["claimId","candidates"],"properties":{"claimId":{"type":"string"},"candidates":{"type":"array","maxItems":2,"items":{"type":"object","additionalProperties":false,"required":["url","sourceClass"],"properties":{"url":{"type":"string"},"sourceClass":{"type":"string","enum":["source_of_record","independent_secondary","unknown_or_ineligible"]}}}}}}}}}
        """;
}

internal sealed record SecondaryPipelineInput(
    string Url,
    string FinalUrl,
    string Title,
    string PageLanguage,
    string ContentDate,
    string PageContent,
    SecondaryStructuralInventory StructuralInventory,
    string OutputLanguage,
    DateOnly AnalysisDate);

internal sealed record SecondaryStructuralInventory(
    SecondaryFormInventory[] Forms,
    SecondaryCtaInventory[] CallsToAction,
    string[] ContactMethods,
    string[] TrustSignals)
{
    public static SecondaryStructuralInventory FromOutline(string outline)
    {
        var forms = System.Text.RegularExpressions.Regex.Matches(outline, @"<form>(.*?)</form>", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select((match, index) => new SecondaryFormInventory(
                $"form-{index + 1}",
                System.Text.RegularExpressions.Regex.Matches(match.Groups[1].Value, @"<field>(.*?)</field>")
                    .Select(field => Normalize(field.Groups[1].Value)).Where(value => value.Length > 0).ToArray(),
                System.Text.RegularExpressions.Regex.Match(match.Groups[1].Value, @"<button[^>]*>(.*?)</button>").Success
                    ? Normalize(System.Text.RegularExpressions.Regex.Match(match.Groups[1].Value, @"<button[^>]*>(.*?)</button>").Groups[1].Value)
                    : null))
            .ToArray();
        var ctas = System.Text.RegularExpressions.Regex.Matches(outline, @"<link href=""([^""]+)"">(.*?)</link>")
            .Select((match, index) => new SecondaryCtaInventory($"link-{index + 1}", Normalize(match.Groups[2].Value), match.Groups[1].Value))
            .Where(item => item.Text.Length > 0).Take(30).ToArray();
        var contacts = ctas.Where(item => item.Href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) || item.Href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Href).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var trust = System.Text.RegularExpressions.Regex.Matches(outline, @"(?:Reviewed by|Author|Bar Association|Last updated)[^\r\n]*", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(match => Normalize(match.Value)).Where(value => value.Length > 0).Take(10).ToArray();
        return new SecondaryStructuralInventory(forms, ctas, contacts, trust);
    }

    private static string Normalize(string value) => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

internal sealed record SecondaryFormInventory(string Id, string[] Fields, string? SubmitControl);
internal sealed record SecondaryCtaInventory(string Id, string Text, string Href);

internal sealed record SecondaryPipelineExecution(
    JsonElement Analysis,
    string Model,
    string RawResponse,
    string[] SourceUrls,
    string? Error);

internal sealed record SecondaryClaimToVerify(
    string Id,
    string Statement,
    string PageQuote,
    string WhyItMatters,
    string VerificationNeed,
    string AuthorityDomain,
    string ResearchQuestion);

internal sealed record SecondaryActionCandidate(
    string Id,
    string Action,
    string ActionType,
    string Entity,
    string Anchor,
    string? BoundAnchorId,
    string PageQuote);

internal sealed record SecondaryTemporalAnchor(
    string Id,
    string Kind,
    string Entity,
    string Aspect,
    int? PeriodYear,
    int? PeriodMonth,
    int? PeriodDay,
    bool GovernsReaderAction,
    string? GovernedActionId,
    string Relation,
    string PageQuote);

internal sealed record SecondaryAnchorResolution(
    string AnchorId,
    string Status,
    DateOnly? ResolvedEndDate,
    string Reason);

internal sealed record SecondaryEvidenceSource(string ClaimId, string Url, string Title, string Content, string ChunkId, string SourceClass);

internal sealed record SecondaryCitedEvidence(string Url, string ChunkId, string Quote, string SourceClass);

internal sealed record SecondaryEvidenceRetrieval(
    IReadOnlyList<SecondaryEvidenceSource> Sources,
    IReadOnlyList<SecondaryRetrievalTrace> Trace);

internal sealed record SecondaryRetrievalTrace(
    string ClaimId,
    string Query,
    string Url,
    string Title,
    string Disposition,
    string? Reason);

internal sealed record SecondaryVerificationResult(
    SecondaryClaimToVerify Claim,
    string Verdict,
    string Reason,
    string? CurrentFact,
    string? SourceUrl,
    string? EvidenceQuote,
    string[] SourceUrls,
    string SourceRole,
    string SourceEligibility)
{
    public static SecondaryVerificationResult NotEnoughEvidence(SecondaryClaimToVerify claim, string reason, string[]? sourceUrls = null) =>
        new(claim, "not_enough_evidence", reason, null, null, null, sourceUrls ?? [], "unknown", "rejected");
}
