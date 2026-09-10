using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SecondaryPipelineInput = VersaCore.FactCheckPipelineInput;

namespace VersaCore;

// Fact checking is a separate product capability. Profiles decide which small
// evidence universe applies; Spanish law is the first profile, not this pipeline's identity.
internal sealed class FactCheckPipeline
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static bool UsesOpenAiWebEvidenceProvider => string.Equals(
        AppConstants.SecondaryEvidenceProvider, "openai_web", StringComparison.OrdinalIgnoreCase);
    private static bool UsesTavilyEvidenceProvider => string.Equals(
        AppConstants.SecondaryEvidenceProvider, "tavily", StringComparison.OrdinalIgnoreCase);

    public async Task<FactCheckPipelineExecution> RunAsync(FactCheckPipelineInput input)
    {
        if (string.IsNullOrWhiteSpace(AppConstants.ApiKey))
            return Failure("OpenAI API key is not configured.");
        if (!UsesOpenAiWebEvidenceProvider && !UsesTavilyEvidenceProvider)
            return Failure($"Unknown secondary evidence provider '{AppConstants.SecondaryEvidenceProvider}'. Use 'openai_web' or 'tavily'.");
        if (UsesTavilyEvidenceProvider && string.IsNullOrWhiteSpace(AppConstants.TavilyApiKey))
            return Failure("Tavily API key is not configured.");

        try
        {
            // The LLM observes assertions from the immutable snapshot; the code adjudicates them.
            var understandingRaw = await RequestUnderstandingAsync(input);
            var understanding = ParseRequiredJson(understandingRaw, "Understanding");
            var intrinsicRaw = await RequestIntrinsicAuditAsync(input, understanding);
            var intrinsic = ParseRequiredJson(intrinsicRaw, "Intrinsic audit");
            var coverageRaw = await RequestAssertionCoverageAsync(input, understanding);
            understanding = MergeCoverageAssertions(understanding, coverageRaw);
            var assertions = ReadAssertions(understanding, input.PageContent, out var rejectedAssertions);
            var verification = new List<SecondaryVerificationResult>();
            var retrievalTrace = new List<SecondaryRetrievalTrace>();
            var sourceSelections = new List<string>();
            var webEvidenceResponses = new List<string>();
            var authorityRoutingRaw = "{\"routes\":[]}";
            var authorityRoutes = new Dictionary<string, SecondaryAuthorityRoute>(StringComparer.Ordinal);
            var selectedByBatch = new List<(SecondaryClaimToVerify[] Claims, IReadOnlyList<SecondaryEvidenceSource> Sources)>();
            var basicSearchRequests = 0;
            var webSearchCalls = 0;

            if (input.EnableExternalVerification && assertions.Count > 0)
            {
                authorityRoutingRaw = await RequestAuthorityRoutingAsync(assertions);
                authorityRoutes = ResolveAuthorityRoutes(authorityRoutingRaw, assertions);
                if (UsesOpenAiWebEvidenceProvider)
                {
                    foreach (var batch in assertions.Chunk(3))
                    {
                        var webResult = await RequestOpenAiWebEvidenceEvaluationAsync(input, batch, authorityRoutes);
                        webSearchCalls += webResult.WebSearchCalls;
                        webEvidenceResponses.Add(webResult.RawResponse);
                        retrievalTrace.AddRange(webResult.Trace);
                        verification.AddRange(ResolveVerificationResults(input, batch, webResult.EvaluationJson, webResult.Evidence));
                    }
                }
                else
                {
                    foreach (var batch in assertions.Chunk(3))
                    {
                        var retrieval = await SearchEvidenceCandidatesAsync(batch, input, authorityRoutes);
                        basicSearchRequests += retrieval.BasicSearchRequests;
                        retrievalTrace.AddRange(retrieval.Trace);
                        var openWebCandidates = retrieval.Sources.Where(source => source.EvidenceMode == "open_web").ToArray();
                        var selectionRaw = openWebCandidates.Length == 0
                            ? "{\"selections\":[]}"
                            : await RequestEvidenceSourceSelectionAsync(batch, openWebCandidates);
                        sourceSelections.Add(selectionRaw);
                        var selected = ResolveSelectedSources(batch, selectionRaw, retrieval.Sources, retrievalTrace);
                        selectedByBatch.Add((batch, selected));
                    }

                    // Extract is intentionally global: a URL selected for multiple claims is fetched once,
                    // in five-URL batches, then attached back to every claim that selected it.
                    var extracted = await ExtractEvidenceAsync(selectedByBatch.SelectMany(item => item.Sources).ToArray(), assertions);
                    retrievalTrace.AddRange(extracted.Trace);
                    foreach (var selectedBatch in selectedByBatch)
                    {
                        var claimIds = selectedBatch.Claims.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                        var evidence = extracted.Sources.Where(item => claimIds.Contains(item.ClaimId)).ToArray();
                        var factRaw = await RequestEvidenceEvaluationAsync(input, selectedBatch.Claims, evidence);
                        verification.AddRange(ResolveVerificationResults(input, selectedBatch.Claims, factRaw, evidence));
                    }
                }
            }

            var retrievalUsage = new SecondaryRetrievalUsage(
                basicSearchRequests,
                selectedByBatch.SelectMany(item => item.Sources).Select(item => item.Url).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                UsesOpenAiWebEvidenceProvider ? "openai_web" : "tavily",
                webSearchCalls);

            var result = ResolveResult(
                input, understanding, intrinsic, assertions, rejectedAssertions, verification, retrievalTrace, retrievalUsage, authorityRoutes);

            return new FactCheckPipelineExecution(
                result,
                AppConstants.DiagnosisModel,
                JsonSerializer.Serialize(new { understanding = understandingRaw, assertionCoverage = coverageRaw, intrinsic = intrinsicRaw, authorityRouting = authorityRoutingRaw, sourceSelections, webEvidenceResponses, verification }, JsonOptions),
                verification.SelectMany(item => item.SourceUrls).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                null);
        }
        catch (Exception exception)
        {
            return Failure(exception.ToString());
        }
    }

    private static FactCheckPipelineExecution Failure(string error)
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
            ["assertions"] = new JsonArray(),
            ["verification"] = new JsonObject { ["claims"] = new JsonArray() },
            ["pipelineError"] = error
        };
        using var document = JsonDocument.Parse(node.ToJsonString(JsonOptions));
        return new FactCheckPipelineExecution(document.RootElement.Clone(), AppConstants.DiagnosisModel, error, [], error);
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

    private static async Task<string> RequestAssertionCoverageAsync(SecondaryPipelineInput input, JsonElement understanding)
    {
        var payload = new { model = AppConstants.IntentModel, temperature = 0, response_format = new { type = "json_schema", json_schema = new { name = "assertion_coverage", strict = true, schema = JsonNode.Parse(AssertionCoverageSchemaJson) } }, messages = new object[] { new { role = "system", content = AssertionCoverageSystemPrompt }, new { role = "user", content = $"existingAssertions: {understanding.GetProperty("assertions").GetRawText()}\npageContent:\n{input.PageContent}" } } };
        return ExtractChatCompletionText(await SendOpenAiAsync("chat/completions", payload));
    }

    private static JsonElement MergeCoverageAssertions(JsonElement understanding, string coverageRaw)
    {
        var root = JsonNode.Parse(understanding.GetRawText())!.AsObject();
        var coverage = ParseRequiredJson(coverageRaw, "Assertion coverage");
        if (coverage.TryGetProperty("missingAssertions", out var missing) && missing.ValueKind == JsonValueKind.Array)
        {
            var assertions = root["assertions"]!.AsArray();
            foreach (var item in missing.EnumerateArray()) assertions.Add(JsonNode.Parse(item.GetRawText()));
        }
        using var doc = JsonDocument.Parse(root.ToJsonString(JsonOptions));
        return doc.RootElement.Clone();
    }

    private static async Task<string> RequestAuthorityRoutingAsync(IReadOnlyList<SecondaryClaimToVerify> assertions)
    {
        var payload = new
        {
            model = AppConstants.IntentModel,
            temperature = 0,
            response_format = new
            {
                type = "json_schema",
                json_schema = new { name = "authority_routing", strict = true, schema = JsonNode.Parse(AuthorityRoutingSchemaJson) }
            },
            messages = new object[]
            {
                new { role = "system", content = AuthorityRoutingSystemPrompt },
                new { role = "user", content = BuildAuthorityRoutingPrompt(assertions) }
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

    // OpenAI Web Search owns retrieval and evaluation in one bounded request. The
    // response is still passed through the same resolver below: a verdict cannot
    // become an issue unless the model cited a native web-search source and that
    // source meets the code-owned eligibility rules.
    private static async Task<SecondaryOpenAiWebEvidenceResult> RequestOpenAiWebEvidenceEvaluationAsync(
        SecondaryPipelineInput input,
        IReadOnlyList<SecondaryClaimToVerify> claims,
        IReadOnlyDictionary<string, SecondaryAuthorityRoute> authorityRoutes)
    {
        var payload = new
        {
            model = AppConstants.SecondaryWebSearchModel,
            tools = new object[] { new { type = "web_search" } },
            tool_choice = "required",
            include = new[] { "web_search_call.action.sources" },
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "web_evidence_evaluation",
                    strict = true,
                    schema = BuildOpenAiWebEvidenceSchema()
                }
            },
            input = new object[]
            {
                new { role = "system", content = OpenAiWebEvidenceEvaluationSystemPrompt },
                new { role = "user", content = BuildOpenAiWebEvidenceEvaluationPrompt(input, claims, authorityRoutes) }
            }
        };
        var raw = await SendOpenAiAsync("responses", payload);
        var evaluationJson = ExtractResponsesText(raw);
        var nativeUrls = ExtractOpenAiWebSourceUrls(raw);
        var parsed = ParseRequiredJson(evaluationJson, "OpenAI Web Search evidence evaluation");
        var sources = BuildOpenAiWebEvidenceSources(parsed, claims, authorityRoutes, nativeUrls, input, out var trace);
        return new SecondaryOpenAiWebEvidenceResult(evaluationJson, sources, trace, CountOpenAiWebSearchCalls(raw), raw);
    }

    private static IReadOnlyList<SecondaryEvidenceSource> BuildOpenAiWebEvidenceSources(
        JsonElement evaluation,
        IReadOnlyList<SecondaryClaimToVerify> claims,
        IReadOnlyDictionary<string, SecondaryAuthorityRoute> authorityRoutes,
        IEnumerable<string> nativeUrls,
        SecondaryPipelineInput input,
        out IReadOnlyList<SecondaryRetrievalTrace> trace)
    {
        var byId = claims.ToDictionary(claim => claim.Id, StringComparer.Ordinal);
        var sources = new List<SecondaryEvidenceSource>();
        var steps = new List<SecondaryRetrievalTrace>();
        if (!evaluation.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Array)
        {
            trace = steps;
            return sources;
        }

        foreach (var check in checks.EnumerateArray())
        {
            var claimId = ReadString(check, "claimId") ?? string.Empty;
            if (!byId.TryGetValue(claimId, out var claim)) continue;
            if (!check.TryGetProperty("evidence", out var evidence) || evidence.ValueKind != JsonValueKind.Array) continue;
            var ordinal = 0;
            foreach (var item in evidence.EnumerateArray())
            {
                var url = ReadString(item, "sourceUrl");
                var quote = ReadString(item, "quote");
                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(quote)) continue;
                ordinal++;
                if (!nativeUrls.Any(nativeUrl => EquivalentWebUrl(nativeUrl, url)))
                {
                    steps.Add(new SecondaryRetrievalTrace(claim.Id, claim.ResearchQuestion, url, string.Empty, "citation_rejected", "URL was not returned by OpenAI Web Search."));
                    continue;
                }
                authorityRoutes.TryGetValue(claim.Id, out var route);
                var sourceClass = ClassifyOpenAiWebSource(claim, route, url, ReadString(item, "sourceRole"));
                steps.Add(new SecondaryRetrievalTrace(claim.Id, claim.ResearchQuestion, url, string.Empty, "web_citation", sourceClass));
                sources.Add(new SecondaryEvidenceSource(
                    claim.Id, url, "OpenAI Web Search citation", quote, $"web-citation-{ordinal}", sourceClass, "openai_web_search"));
            }
        }
        trace = steps;
        return sources;
    }

    private static string ClassifyOpenAiWebSource(SecondaryClaimToVerify claim, SecondaryAuthorityRoute? route, string url, string? declaredRole)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "unknown";
        var host = uri.Host;
        var isDirectoryAuthority = route?.Domains.Any(domain => HostMatchesDomain(host, domain)) == true;
        var isDirectActionDestination = !string.IsNullOrWhiteSpace(claim.DirectSourceUrl)
            && Uri.TryCreate(claim.DirectSourceUrl, UriKind.Absolute, out var destination)
            && string.Equals(host, destination.Host, StringComparison.OrdinalIgnoreCase);
        if (isDirectoryAuthority || isDirectActionDestination || IsGenericPublicAuthority(host)) return "source_of_record";
        // A cited search result can be an authority whose domain is not syntactically
        // governmental (for example, a transport authority). This is an LLM
        // observation, but it is accepted only together with the tool-native URL.
        if (declaredRole == "source_of_record") return "source_of_record";
        return claim.Kind == "historical_statement" && declaredRole == "independent_secondary"
            ? "independent_secondary"
            : "unknown";
    }

    private static bool HostMatchesDomain(string host, string domain) =>
        string.Equals(host, domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase);

    // This is a source-role rule, not a topic-specific knowledge base. It allows
    // a public authority discovered outside our still-small curated directory.
    private static bool IsGenericPublicAuthority(string host) =>
        host.EndsWith(".gov", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".gov.uk", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".gob.es", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".europa.eu", StringComparison.OrdinalIgnoreCase);

    private static bool EquivalentWebUrl(string left, string right) =>
        Uri.TryCreate(left, UriKind.Absolute, out var leftUri)
        && Uri.TryCreate(right, UriKind.Absolute, out var rightUri)
        && string.Equals(leftUri.Host, rightUri.Host, StringComparison.OrdinalIgnoreCase)
        && string.Equals(leftUri.AbsolutePath.TrimEnd('/'), rightUri.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static string[] ExtractOpenAiWebSourceUrls(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectOpenAiWebSourceUrls(document.RootElement, urls);
        return urls.ToArray();
    }

    private static void CollectOpenAiWebSourceUrls(JsonElement value, ISet<string> urls)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() is "url" or "url_citation"
                && value.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                urls.Add(url.GetString()!);
            foreach (var property in value.EnumerateObject()) CollectOpenAiWebSourceUrls(property.Value, urls);
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CollectOpenAiWebSourceUrls(item, urls);
    }

    private static int CountOpenAiWebSearchCalls(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return CountOpenAiWebSearchCalls(document.RootElement);
    }

    private static int CountOpenAiWebSearchCalls(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => (value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "web_search_call" ? 1 : 0)
            + value.EnumerateObject().Sum(property => CountOpenAiWebSearchCalls(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().Sum(CountOpenAiWebSearchCalls),
        _ => 0
    };

    private static JsonNode BuildOpenAiWebEvidenceSchema()
    {
        var schema = JsonNode.Parse(EvidenceEvaluationSchemaJson)!.AsObject();
        var evidenceItem = schema["properties"]!["checks"]!["items"]!["properties"]!["evidence"]!["items"]!.AsObject();
        evidenceItem["required"]!.AsArray().Add("sourceRole");
        evidenceItem["properties"]!["sourceRole"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray("source_of_record", "independent_secondary", "unknown")
        };
        return schema;
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
        SecondaryPipelineInput input,
        IReadOnlyDictionary<string, SecondaryAuthorityRoute> authorityRoutes)
    {
        using var client = new HttpClient { BaseAddress = new Uri(AppConstants.TavilyBaseUrl), Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AppConstants.TavilyApiKey);
        var sources = new List<SecondaryEvidenceSource>();
        var trace = new List<SecondaryRetrievalTrace>();

        var searchTasks = claims.Select(async claim =>
        {
            try
            {
                return await SearchEvidenceCandidatesForClaimAsync(client, claim, input, authorityRoutes.GetValueOrDefault(claim.Id));
            }
            catch (Exception exception)
            {
                return FailedRetrieval(claim, "search_failed", exception.Message);
            }
        });
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
            trace,
            searchResults.Sum(result => result.BasicSearchRequests));
    }

    private static async Task<SecondaryEvidenceRetrieval> SearchEvidenceCandidatesForClaimAsync(
        HttpClient client,
        SecondaryClaimToVerify claim,
        SecondaryPipelineInput input,
        SecondaryAuthorityRoute? route)
    {
        var sources = new List<SecondaryEvidenceSource>();
        var trace = new List<SecondaryRetrievalTrace>();
        var searchQuery = BuildStructuredSearchQuery(claim);
        if (!string.IsNullOrWhiteSpace(claim.DirectSourceUrl))
        {
            const string directDestinationTitle = "Direct destination linked from the audited page";
            sources.Add(new SecondaryEvidenceSource(
                claim.Id,
                claim.DirectSourceUrl,
                directDestinationTitle,
                "This is the visible destination offered by the audited page for the reader action.",
                "direct-action-destination",
                "source_of_record",
                "first_party_action_destination"));
            trace.Add(new SecondaryRetrievalTrace(
                claim.Id,
                searchQuery,
                claim.DirectSourceUrl,
                directDestinationTitle,
                "candidate",
                "direct_action_destination"));
        }

        var authoritySources = route is null
            ? Array.Empty<SecondaryEvidenceSource>()
            : await SearchTavilyAsync(client, claim, input, searchQuery, route.Domains, "authority_directory", "source_of_record", trace);
        sources.AddRange(authoritySources);

        // Open-web retrieval is a discovery fallback. It may help surface a future
        // directory entry, but its evidence cannot issue a definitive verdict.
        var requiresOpenWebFallback = string.IsNullOrWhiteSpace(claim.DirectSourceUrl)
            && (route is null || authoritySources.Length == 0);
        if (requiresOpenWebFallback)
        {
            var openWebSources = await SearchTavilyAsync(client, claim, input, searchQuery, null, "open_web", "unknown", trace);
            sources.AddRange(openWebSources);
        }
        var requestCount = route is not null ? 1 : 0;
        if (requiresOpenWebFallback) requestCount++;
        return new SecondaryEvidenceRetrieval(sources, trace, requestCount);
    }

    private static async Task<SecondaryEvidenceSource[]> SearchTavilyAsync(
        HttpClient client,
        SecondaryClaimToVerify claim,
        SecondaryPipelineInput input,
        string searchQuery,
        string[]? includeDomains,
        string evidenceMode,
        string sourceClass,
        ICollection<SecondaryRetrievalTrace> trace)
    {
        var auditedHost = new Uri(input.FinalUrl).Host;
        var payload = new
        {
            query = searchQuery,
            search_depth = "basic",
            max_results = 5,
            include_raw_content = false,
            include_answer = false,
            auto_parameters = false,
            include_domains = includeDomains,
            exclude_domains = new[] { auditedHost }
        };
        using var response = await client.PostAsync("search", new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"));
        var raw = await response.Content.ReadAsStringAsync();
        var sourceScope = includeDomains is null ? "open_web" : string.Join(",", includeDomains);
        if (!response.IsSuccessStatusCode)
        {
            trace.Add(new SecondaryRetrievalTrace(claim.Id, searchQuery, string.Empty, string.Empty, "search_failed", $"{sourceScope}: HTTP {(int)response.StatusCode}"));
            return [];
        }
        using var document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return [];
        var sources = new List<SecondaryEvidenceSource>();
        foreach (var item in results.EnumerateArray())
        {
            var url = ReadString(item, "url");
            var title = ReadString(item, "title") ?? url ?? string.Empty;
            var content = ReadString(item, "content");
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(content)) continue;
            var score = item.TryGetProperty("score", out var scoreNode) && scoreNode.TryGetDouble(out var value) ? value : 0;
            trace.Add(new SecondaryRetrievalTrace(claim.Id, searchQuery, url, title, "candidate", $"{evidenceMode}; score={score:F2}"));
            sources.Add(new SecondaryEvidenceSource(claim.Id, url, title, NormalizeWhitespace(content), "search-snippet", sourceClass, evidenceMode));
        }
        return sources.ToArray();
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
            var automaticSelections = 0;
            foreach (var candidate in candidates.Where(item => item.ClaimId == claim.Id))
            {
                if (candidate.EvidenceMode is "authority_directory" or "first_party_action_destination")
                {
                    if (automaticSelections >= 2)
                    {
                        trace.Add(new SecondaryRetrievalTrace(claim.Id, claim.ResearchQuestion, candidate.Url, candidate.Title, "rejected_for_extract", "authority_candidate_limit"));
                        continue;
                    }
                    trace.Add(new SecondaryRetrievalTrace(claim.Id, claim.ResearchQuestion, candidate.Url, candidate.Title, "selected_for_extract", candidate.EvidenceMode));
                    selected.Add(candidate);
                    automaticSelections++;
                    continue;
                }
                if (!urls.TryGetValue(candidate.Url, out var sourceClass))
                {
                    trace.Add(new SecondaryRetrievalTrace(claim.Id, claim.ResearchQuestion, candidate.Url, candidate.Title, "rejected_for_extract", null));
                    continue;
                }
                if (!IsEligibleForExtract(claim.Kind, sourceClass))
                {
                    trace.Add(new SecondaryRetrievalTrace(claim.Id, claim.ResearchQuestion, candidate.Url, candidate.Title, "rejected_for_extract", "ineligible_source_class"));
                    continue;
                }
                trace.Add(new SecondaryRetrievalTrace(claim.Id, claim.ResearchQuestion, candidate.Url, candidate.Title, "selected_for_extract", sourceClass));
                selected.Add(candidate with { SourceClass = sourceClass });
            }
        }
        return selected;
    }

    private static bool IsEligibleForExtract(string assertionKind, string sourceClass) =>
        sourceClass == "source_of_record"
        || assertionKind == "historical_statement" && sourceClass == "independent_secondary";

    private static async Task<SecondaryEvidenceRetrieval> ExtractEvidenceAsync(
        IReadOnlyList<SecondaryEvidenceSource> selected,
        IReadOnlyList<SecondaryClaimToVerify> assertions)
    {
        if (selected.Count == 0) return new SecondaryEvidenceRetrieval([], []);
        using var client = new HttpClient { BaseAddress = new Uri(AppConstants.TavilyBaseUrl), Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AppConstants.TavilyApiKey);
        var queryByClaim = assertions.ToDictionary(item => item.Id, BuildStructuredSearchQuery, StringComparer.Ordinal);
        var tasks = selected
            .GroupBy(item => queryByClaim.GetValueOrDefault(item.ClaimId) ?? string.Empty, StringComparer.Ordinal)
            .SelectMany(group => group.GroupBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
                .Select(urlGroup => urlGroup.ToArray()).Chunk(5)
                .Select(async batch =>
                {
                    try
                    {
                        return await ExtractEvidenceBatchAsync(client, batch, group.Key);
                    }
                    catch (Exception exception)
                    {
                        return FailedExtraction(batch, exception.Message);
                    }
                }));
        var extracted = await Task.WhenAll(tasks);
        return new SecondaryEvidenceRetrieval(extracted.SelectMany(item => item.Sources).ToArray(), extracted.SelectMany(item => item.Trace).ToArray());
    }

    private static async Task<SecondaryEvidenceRetrieval> ExtractEvidenceBatchAsync(
        HttpClient client,
        IReadOnlyList<SecondaryEvidenceSource[]> selectedByUrl,
        string query)
    {
        var payload = new { urls = selectedByUrl.Select(item => item[0].Url).ToArray(), query, chunks_per_source = 3, extract_depth = "basic", format = "markdown" };
        using var response = await client.PostAsync("extract", new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"));
        var raw = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Tavily extract returned HTTP {(int)response.StatusCode}: {raw}");
        using var document = JsonDocument.Parse(raw);
        var results = document.RootElement.TryGetProperty("results", out var values) && values.ValueKind == JsonValueKind.Array ? values : default;
        var sources = new List<SecondaryEvidenceSource>();
        var trace = new List<SecondaryRetrievalTrace>();
        if (results.ValueKind == JsonValueKind.Array)
            foreach (var item in results.EnumerateArray())
            {
                var url = ReadString(item, "url");
                var content = ReadString(item, "raw_content");
                var originals = selectedByUrl.FirstOrDefault(group => string.Equals(group[0].Url, url, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(content) || originals is null) continue;
                var chunks = content.Split("[...]", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                for (var index = 0; index < chunks.Length; index++)
                {
                    var chunk = NormalizeWhitespace(chunks[index]);
                    if (chunk.Length == 0) continue;
                    var chunkId = $"chunk-{index + 1}";
                    foreach (var original in originals)
                    {
                        trace.Add(new SecondaryRetrievalTrace(original.ClaimId, string.Empty, url, original.Title, "extracted", chunkId));
                        sources.Add(new SecondaryEvidenceSource(original.ClaimId, url, original.Title, chunk, chunkId, original.SourceClass, original.EvidenceMode));
                    }
                }
            }
        return new SecondaryEvidenceRetrieval(sources, trace);
    }

    private static SecondaryEvidenceRetrieval FailedRetrieval(SecondaryClaimToVerify claim, string disposition, string reason) =>
        new([], [new SecondaryRetrievalTrace(claim.Id, claim.ResearchQuestion, string.Empty, string.Empty, disposition, reason)]);

    private static SecondaryEvidenceRetrieval FailedExtraction(IReadOnlyList<SecondaryEvidenceSource[]> selectedByUrl, string reason) =>
        new([], selectedByUrl.SelectMany(group => group)
            .Select(source => new SecondaryRetrievalTrace(source.ClaimId, string.Empty, source.Url, source.Title, "extract_failed", reason))
            .ToArray());

    private static IReadOnlyList<SecondaryClaimToVerify> ReadAssertions(JsonElement understanding, string pageContent, out IReadOnlyList<JsonObject> rejected)
    {
        var assertions = new List<SecondaryClaimToVerify>();
        var rejectedItems = new List<JsonObject>();
        var normalizedSnapshot = VisibleSnapshotText(pageContent);
        if (!understanding.TryGetProperty("assertions", out var values) || values.ValueKind != JsonValueKind.Array)
        { rejected = rejectedItems; return assertions; }
        foreach (var item in values.EnumerateArray())
        {
            var observationKind = ReadString(item, "kind") ?? string.Empty;
            var framing = ReadString(item, "pageFraming") ?? "neutral";
            var kind = observationKind == "reader_action" ? "reader_action"
                : framing == "historical_record" ? "historical_statement"
                : "current_guidance";
            var quote = NormalizeWhitespace(ReadString(item, "pageQuote"));
            if (quote.Length == 0 || !normalizedSnapshot.Contains(quote, StringComparison.Ordinal))
            { rejectedItems.Add(new JsonObject { ["id"] = ReadString(item, "id"), ["reason"] = "quote_not_in_snapshot", ["pageQuote"] = ReadString(item, "pageQuote") }); continue; }
            assertions.Add(new SecondaryClaimToVerify(
                    ReadString(item, "id") ?? $"assertion-{assertions.Count + 1}",
                    kind,
                    ReadString(item, "subject") ?? string.Empty,
                    ReadString(item, "aspect") ?? string.Empty,
                    ReadString(item, "timeAnchorText") ?? ReadString(item, "timeAnchorEndDate"),
                    ReadString(item, "jurisdiction") ?? string.Empty,
                    quote,
                    ReadString(item, "whyMaterial") ?? string.Empty,
                    ReadString(item, "researchQuestion") ?? string.Empty,
                    ReadString(item, "sourceQuery") ?? string.Empty,
                    kind == "reader_action" ? ReadDirectUrl(item, "targetUrl") : null));
        }
        rejected = rejectedItems;
        return assertions.GroupBy(assertion => assertion.Id, StringComparer.Ordinal).Select(group => group.First()).ToArray();
    }

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
            var temporalEvidence = ReadTemporalEvidence(check, claim, evidence);
            var verdict = NormalizeTemporalVerdict(
                claim,
                ReadString(check, "verdict") ?? "not_enough_evidence",
                ReadString(check, "temporalRelation"),
                temporalEvidence);
            if (!IsResolutionAllowed(claim.Kind, verdict))
            {
                results.Add(SecondaryVerificationResult.NotEnoughEvidence(
                    claim,
                    "The evidence evaluator returned a resolution incompatible with this assertion type.",
                    consultedUrls));
                continue;
            }
            if (verdict != "not_enough_evidence" && ReadString(check, "identityMatch") != "exact")
            {
                results.Add(SecondaryVerificationResult.NotEnoughEvidence(
                    claim,
                    "The supplied evidence does not exactly match the assertion's subject, aspect, and jurisdiction.",
                    consultedUrls));
                continue;
            }
            if (!HasRequiredCurrentEvidenceStatus(
                    claim.Kind,
                    verdict,
                    ReadString(check, "temporalRelation"),
                    temporalEvidence,
                    input.AnalysisDate))
            {
                results.Add(SecondaryVerificationResult.NotEnoughEvidence(
                    claim,
                    "The evidence does not explicitly establish the assertion's status at the analysis date.", consultedUrls));
                continue;
            }
            var citedEvidence = ReadCitedEvidence(check, claim, evidence);
            if (citedEvidence.Count == 0 && verdict != "not_enough_evidence")
            {
                results.Add(SecondaryVerificationResult.NotEnoughEvidence(
                    claim,
                    "The evaluator did not provide a literal evidence quote inside an extracted Tavily chunk.", consultedUrls));
                continue;
            }
            var hasSourceOfRecord = citedEvidence.Any(item => item.SourceClass == "source_of_record"
                && item.EvidenceMode is "authority_directory" or "first_party_action_destination" or "openai_web_search");
            var independentSecondaryCount = citedEvidence.Where(item => item.SourceClass == "independent_secondary")
                .Select(item => new Uri(item.Url).Host).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var evidenceIsSufficient = claim.Kind == "historical_statement"
                ? hasSourceOfRecord || independentSecondaryCount >= 2
                : hasSourceOfRecord;
            if (verdict != "not_enough_evidence" && !evidenceIsSufficient)
            {
                results.Add(SecondaryVerificationResult.NotEnoughEvidence(
                    claim,
                    claim.Kind == "historical_statement"
                        ? "A historical assertion requires a source of record or two independent secondary sources."
                        : "A current or operational assertion requires a source of record.", consultedUrls));
                continue;
            }
            if (citedEvidence.Any(item => IsAuditedPageOrVariant(item.Url, input)))
            {
                results.Add(SecondaryVerificationResult.NotEnoughEvidence(
                    claim,
                    "A historical copy, canonical variant, or AMP variant of the audited page cannot prove the assertion.", consultedUrls));
                continue;
            }
            if (verdict == "not_enough_evidence")
            {
                results.Add(SecondaryVerificationResult.NotEnoughEvidence(
                    claim,
                    ReadString(check, "reason") ?? "The evidence packet does not establish the claim.",
                    consultedUrls));
                continue;
            }
            var temporalCitedEvidence = temporalEvidence is null ? null : evidence.FirstOrDefault(item => item.ClaimId == claim.Id
                && string.Equals(item.Url, temporalEvidence.Url, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ChunkId, temporalEvidence.ChunkId, StringComparison.Ordinal));
            var selectedEvidence = verdict is "unavailable" or "changed" && temporalCitedEvidence is not null
                ? new SecondaryCitedEvidence(temporalCitedEvidence.Url, temporalCitedEvidence.ChunkId, temporalEvidence!.Quote, temporalCitedEvidence.SourceClass, temporalCitedEvidence.EvidenceMode)
                : citedEvidence.FirstOrDefault(item => item.SourceClass == "source_of_record"
                    && item.EvidenceMode is "authority_directory" or "first_party_action_destination" or "openai_web_search") ?? citedEvidence.FirstOrDefault();
            results.Add(new SecondaryVerificationResult(
                claim,
                verdict,
                ReadString(check, "reason") ?? string.Empty,
                ReadString(check, "currentFact"),
                selectedEvidence?.Url,
                selectedEvidence?.Quote,
                citedEvidence.Count == 0 ? consultedUrls : citedEvidence.Select(item => item.Url).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                selectedEvidence?.SourceClass == "source_of_record" ? "primary_authority" : selectedEvidence?.SourceClass == "independent_secondary" ? "secondary" : "unknown",
                evidenceIsSufficient ? "accepted" : "rejected",
                selectedEvidence?.EvidenceMode ?? "inconclusive"));
        }
        return results;
    }

    private static bool IsResolutionAllowed(string kind, string resolution) =>
        resolution == "not_enough_evidence" || kind switch
        {
            "historical_statement" => resolution is "true_in_context" or "false_in_context",
            "current_guidance" => resolution is "applicable" or "unavailable" or "changed",
            "reader_action" => resolution is "available" or "unavailable" or "changed",
            _ => false
        };

    // This is adjudication, not a page-specific heuristic: a dated action or
    // instruction cannot remain the same available proposition when the evidence
    // explicitly identifies a later or different cycle.
    private static string NormalizeTemporalVerdict(
        SecondaryClaimToVerify claim,
        string verdict,
        string? temporalRelation,
        SecondaryTemporalEvidence? temporalEvidence) =>
        claim.Kind is "reader_action" or "current_guidance"
        && verdict is "available" or "applicable"
        && (temporalRelation is "later" or "different" || temporalEvidence?.Status == "explicitly_ended")
            ? "changed"
            : verdict;

    private static bool HasRequiredCurrentEvidenceStatus(
        string kind,
        string verdict,
        string? temporalRelation,
        SecondaryTemporalEvidence? temporalEvidence,
        DateOnly analysisDate)
    {
        if (verdict == "not_enough_evidence" || kind == "historical_statement") return true;
        if (temporalEvidence is null) return false;
        if (temporalEvidence.EffectiveEndDate is { } endDate && endDate < analysisDate && verdict is "applicable" or "available") return false;
        return verdict switch
        {
            "applicable" or "available" => temporalEvidence.Status == "explicitly_current",
            "unavailable" => temporalEvidence.Status == "explicitly_ended",
            "changed" => temporalEvidence.Status is "explicitly_current" or "explicitly_ended"
                || temporalRelation is "later" or "different",
            _ => false
        };
    }

    private static SecondaryTemporalEvidence? ReadTemporalEvidence(
        JsonElement check,
        SecondaryClaimToVerify claim,
        IReadOnlyList<SecondaryEvidenceSource> packet)
    {
        if (!check.TryGetProperty("temporalEvidence", out var value) || value.ValueKind != JsonValueKind.Object) return null;
        var url = ReadString(value, "sourceUrl");
        var chunkId = ReadString(value, "chunkId");
        var quote = NormalizeWhitespace(ReadString(value, "quote"));
        var status = ReadString(value, "status");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(chunkId) || quote.Length < 20 || string.IsNullOrWhiteSpace(status)) return null;
        var source = packet.FirstOrDefault(candidate => MatchesEvidenceReference(candidate, claim.Id, url, chunkId, quote));
        if (source is null || !source.Content.Contains(quote, StringComparison.OrdinalIgnoreCase)) return null;
        var endDate = DateOnly.TryParse(ReadString(value, "effectiveEndDate"), out var parsed) ? parsed : (DateOnly?)null;
        return new SecondaryTemporalEvidence(url, chunkId, quote, status, endDate);
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
            var source = packet.FirstOrDefault(candidate => MatchesEvidenceReference(candidate, claim.Id, url, chunkId, quote));
            if (source is null || !source.Content.Contains(quote, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(new SecondaryCitedEvidence(source.Url, source.ChunkId, quote, source.SourceClass, source.EvidenceMode));
        }
        return result;
    }

    private static bool MatchesEvidenceReference(
        SecondaryEvidenceSource candidate,
        string claimId,
        string url,
        string chunkId,
        string quote) =>
        candidate.ClaimId == claimId
        && EquivalentWebUrl(candidate.Url, url)
        // Native Web Search citations do not expose the provider's internal chunk
        // identifier. The URL and literal quote are the stable audit references.
        && (candidate.EvidenceMode == "openai_web_search"
            || string.Equals(candidate.ChunkId, chunkId, StringComparison.Ordinal))
        && candidate.Content.Contains(quote, StringComparison.OrdinalIgnoreCase);

    private static JsonElement ResolveResult(
        SecondaryPipelineInput input,
        JsonElement understanding,
        JsonElement intrinsic,
        IReadOnlyList<SecondaryClaimToVerify> assertions,
        IReadOnlyList<JsonObject> rejectedAssertions,
        IReadOnlyList<SecondaryVerificationResult> verification,
        IReadOnlyList<SecondaryRetrievalTrace> retrievalTrace,
        SecondaryRetrievalUsage retrievalUsage,
        IReadOnlyDictionary<string, SecondaryAuthorityRoute> authorityRoutes)
    {
        var root = JsonNode.Parse(understanding.GetRawText())!.AsObject();
        var issues = JsonNode.Parse(intrinsic.GetRawText())?["issues"]?.AsArray().DeepClone().AsArray() ?? new JsonArray();
        foreach (var issue in issues.OfType<JsonObject>()) issue["source"] = "page";
        foreach (var result in verification)
        {
            var code = result.Claim.Kind switch
            {
                "reader_action" when result.Verdict is "unavailable" or "changed" => "expired_date_or_deadline",
                "current_guidance" when result.Verdict is "unavailable" or "changed" => "verified_current_claim_issue",
                "historical_statement" when result.Verdict == "false_in_context" => "verified_historical_claim_issue",
                _ => null
            };
            if (code is not null)
                issues.Add(new JsonObject
                {
                    ["code"] = code,
                    ["source"] = result.EvidenceMode == "openai_web_search" ? "openai_web_fact_check" : "tavily_fact_check",
                    ["assertionId"] = result.Claim.Id,
                    ["reason"] = result.Reason,
                    ["pageQuotes"] = new JsonArray(result.Claim.PageQuote),
                    ["currentFact"] = result.CurrentFact,
                    ["evidence"] = new JsonObject { ["sourceUrl"] = result.SourceUrl, ["quote"] = result.EvidenceQuote }
                });
            if (result.Claim.Kind == "reader_action" && result.Verdict == "not_enough_evidence"
                && result.SourceUrls.Length > 0 && IsStaleRisk(input.ContentDate))
                issues.Add(new JsonObject
                {
                    ["code"] = "stale_risk",
                    ["source"] = result.EvidenceMode == "openai_web_search" ? "openai_web_fact_check" : "tavily_fact_check",
                    ["assertionId"] = result.Claim.Id,
                    ["reason"] = "The page makes an old operational promise whose current availability could not be resolved from consulted sources.",
                    ["pageQuotes"] = new JsonArray(result.Claim.PageQuote),
                    ["sourcesConsulted"] = new JsonArray(result.SourceUrls.Select(url => (JsonNode?)url).ToArray())
                });
        }

        root["issues"] = issues;
        root["assertions"] = new JsonArray(assertions.Select(assertion => JsonSerializer.SerializeToNode(assertion, JsonOptions)).ToArray());
        root["rejectedAssertions"] = new JsonArray(rejectedAssertions.Select(item => (JsonNode?)item).ToArray());
        root["verification"] = new JsonObject
        {
            ["claims"] = new JsonArray(verification.Select(item => JsonSerializer.SerializeToNode(item, JsonOptions)).ToArray()),
            ["retrieval"] = new JsonArray(retrievalTrace.Select(item => JsonSerializer.SerializeToNode(item, JsonOptions)).ToArray()),
            ["summary"] = new JsonObject
            {
                ["submitted"] = verification.Count,
                ["validated"] = verification.Count(item => item.SourceUrl is not null),
                ["notEnoughEvidence"] = verification.Count(item => item.Verdict == "not_enough_evidence")
            },
            ["usage"] = JsonSerializer.SerializeToNode(retrievalUsage, JsonOptions)
        };
        root["externalVerification"] = new JsonObject
        {
            ["enabled"] = input.EnableExternalVerification,
            ["skipped"] = !input.EnableExternalVerification
        };
        root["authorityRouting"] = new JsonArray(assertions.Select(assertion =>
        {
            authorityRoutes.TryGetValue(assertion.Id, out var route);
            return (JsonNode?)new JsonObject
            {
                ["assertionId"] = assertion.Id,
                ["collectionIds"] = new JsonArray((route?.CollectionIds ?? []).Select(id => (JsonNode?)id).ToArray()),
                ["domains"] = new JsonArray((route?.Domains ?? []).Select(domain => (JsonNode?)domain).ToArray()),
                ["mode"] = route is null ? "open_web" : "authority_directory",
                ["reason"] = route?.Reason ?? "No matching authority collection was selected."
            };
        }).ToArray());
        var hasExternalAssertions = assertions.Count > 0;
        root["classification"] = new JsonObject
        {
            ["type"] = issues.Count > 0 ? "unhealthy" : !input.EnableExternalVerification && hasExternalAssertions ? "inconclusive" : "healthy",
            ["confidence"] = issues.Count > 0 ? 0.95 : !input.EnableExternalVerification && hasExternalAssertions ? 0 : 0.8,
            ["insufficientEvidence"] = !input.EnableExternalVerification && hasExternalAssertions || verification.Any(item => item.Verdict == "not_enough_evidence")
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

        structuralInventory (visible controls extracted from the same snapshot):
        {JsonSerializer.Serialize(input.StructuralInventory, JsonOptions)}

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

        readerActions (observations extracted from the same visible snapshot; they are not a health verdict):
        {ReadReaderActions(understanding)}

        pageContent:
        {input.PageContent}
        """;

    private static string ReadReaderActions(JsonElement understanding) =>
        understanding.TryGetProperty("assertions", out var assertions) && assertions.ValueKind == JsonValueKind.Array
            ? new JsonArray(assertions.EnumerateArray()
                .Where(item => ReadString(item, "kind") == "reader_action")
                .Select(item => JsonNode.Parse(item.GetRawText())).ToArray()).ToJsonString(JsonOptions)
            : "[]";

    private static string BuildEvidenceEvaluationPrompt(
        SecondaryPipelineInput input,
        IReadOnlyList<SecondaryClaimToVerify> claims,
        IReadOnlyList<SecondaryEvidenceSource> evidence)
    {
        var builder = new StringBuilder($"analysisDate: {input.AnalysisDate:yyyy-MM-dd}\nauditedUrl: {input.FinalUrl}\nclaims:\n");
        foreach (var claim in claims)
        {
            builder.AppendLine($"- id: {claim.Id}");
            builder.AppendLine($"  kind: {claim.Kind}");
            builder.AppendLine($"  subject: {claim.Subject}");
            builder.AppendLine($"  aspect: {claim.Aspect}");
            builder.AppendLine($"  timeReference: {claim.TimeReference}");
            builder.AppendLine($"  jurisdiction: {claim.Jurisdiction}");
            builder.AppendLine($"  pageQuote: {claim.PageQuote}");
            builder.AppendLine($"  researchQuestion: {claim.ResearchQuestion}");
            builder.AppendLine($"  sourceQuery: {claim.SourceQuery}");
        }
        builder.AppendLine("Tavily evidence packet:");
        foreach (var source in evidence)
        {
            builder.AppendLine($"- claimId: {source.ClaimId}");
            builder.AppendLine($"- url: {source.Url}");
            builder.AppendLine($"  chunkId: {source.ChunkId}");
            builder.AppendLine($"  sourceClass: {source.SourceClass}");
            builder.AppendLine($"  evidenceMode: {source.EvidenceMode}");
            builder.AppendLine($"  title: {source.Title}");
            builder.AppendLine($"  content: {Truncate(source.Content, 6000)}");
        }
        return builder.ToString();
    }

    private static string BuildOpenAiWebEvidenceEvaluationPrompt(
        SecondaryPipelineInput input,
        IReadOnlyList<SecondaryClaimToVerify> claims,
        IReadOnlyDictionary<string, SecondaryAuthorityRoute> authorityRoutes)
    {
        var builder = new StringBuilder($"analysisDate: {input.AnalysisDate:yyyy-MM-dd}\nauditedUrl: {input.FinalUrl}\nassertions:\n");
        foreach (var claim in claims)
        {
            authorityRoutes.TryGetValue(claim.Id, out var route);
            builder.AppendLine($"- id: {claim.Id}");
            builder.AppendLine($"  kind: {claim.Kind}");
            builder.AppendLine($"  subject: {claim.Subject}");
            builder.AppendLine($"  aspect: {claim.Aspect}");
            builder.AppendLine($"  timeReference: {claim.TimeReference}");
            builder.AppendLine($"  jurisdiction: {claim.Jurisdiction}");
            builder.AppendLine($"  pageQuote: {claim.PageQuote}");
            builder.AppendLine($"  researchQuestion: {claim.ResearchQuestion}");
            builder.AppendLine($"  preferredAuthorityDomains: {string.Join(", ", route?.Domains ?? [])}");
            builder.AppendLine($"  directActionDestination: {claim.DirectSourceUrl ?? "null"}");
        }
        builder.AppendLine("Use Web Search for each assertion. Cite only URLs returned by that tool. Prefer the listed authority domains; when none apply, prefer the competent public authority, original publisher, organiser, or the exact first-party action destination. Do not use the audited page or a canonical/AMP variant as evidence.");
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
            builder.AppendLine($"  kind: {claim.Kind}");
            builder.AppendLine($"  subject: {claim.Subject}");
            builder.AppendLine($"  aspect: {claim.Aspect}");
            builder.AppendLine($"  timeReference: {claim.TimeReference}");
            builder.AppendLine($"  jurisdiction: {claim.Jurisdiction}");
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

    private static Dictionary<string, SecondaryAuthorityRoute> ResolveAuthorityRoutes(
        string routingRaw,
        IReadOnlyList<SecondaryClaimToVerify> assertions)
    {
        var parsed = ParseRequiredJson(routingRaw, "Authority routing");
        var assertionIds = assertions.Select(assertion => assertion.Id).ToHashSet(StringComparer.Ordinal);
        var knownCollectionIds = AuthorityDirectory.Collections.Select(collection => collection.Id).ToHashSet(StringComparer.Ordinal);
        if (!parsed.TryGetProperty("routes", out var routes) || routes.ValueKind != JsonValueKind.Array)
            return new Dictionary<string, SecondaryAuthorityRoute>(StringComparer.Ordinal);
        return routes.EnumerateArray()
            .Select(route => new SecondaryAuthorityRoute(
                ReadString(route, "assertionId") ?? string.Empty,
                route.TryGetProperty("collectionIds", out var ids) && ids.ValueKind == JsonValueKind.Array
                    ? ids.EnumerateArray().Select(item => item.GetString() ?? string.Empty)
                        .Where(knownCollectionIds.Contains).Distinct(StringComparer.Ordinal).ToArray()
                    : [],
                ReadString(route, "reason") ?? string.Empty))
            .Where(route => assertionIds.Contains(route.AssertionId) && route.CollectionIds.Length > 0)
            .GroupBy(route => route.AssertionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    private static string BuildAuthorityRoutingPrompt(IReadOnlyList<SecondaryClaimToVerify> assertions) => $"""
        authorityCollections:
        {JsonSerializer.Serialize(AuthorityDirectory.Collections, JsonOptions)}

        assertions:
        {JsonSerializer.Serialize(assertions.Select(assertion => new
        {
            assertion.Id,
            assertion.Kind,
            assertion.Subject,
            assertion.Aspect,
            assertion.TimeReference,
            assertion.Jurisdiction,
            assertion.PageQuote,
            assertion.SourceQuery
        }), JsonOptions)}
        """;

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

    private static string? ReadDirectUrl(JsonElement element, string property)
    {
        var value = ReadString(element, property);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsoluteUri
            : null;
    }

    private static string NormalizeWhitespace(string? value) =>
        string.Join(" ", (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // Quotes describe visible reader-facing text. The analysis snapshot contains structural tags
    // such as <link>, which must not break a contiguous visible quote during validation.
    private static string VisibleSnapshotText(string value) =>
        NormalizeWhitespace(System.Text.RegularExpressions.Regex.Replace(value, @"<[^>]+>", " "));

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    private static string BuildStructuredSearchQuery(SecondaryClaimToVerify assertion)
    {
        if (!string.IsNullOrWhiteSpace(assertion.SourceQuery)) return assertion.SourceQuery;
        var statusIntent = assertion.Kind switch
        {
            "historical_statement" => "historical record",
            "reader_action" => "currently available",
            _ => "currently applicable"
        };
        return string.Join(" ", new[]
        {
            assertion.Subject,
            assertion.Aspect,
            assertion.TimeReference,
            statusIntent
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

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
        You are a page-observation sensor in an auditable web-content pipeline. Read one immutable page snapshot and return only its purpose, intent, summary, explicit operational actions, and material factual assertions. Do not diagnose issues, decide whether anything is active or expired, infer dates, or use knowledge outside the supplied snapshot.

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

        Extract observations in this exact order. These are observations only: do not decide whether a page is healthy, expired, false, or current.
        Extract one unified assertions array, up to ten items. Extract every distinct material rule, eligibility condition, rate, deadline, exemption, availability statement, technical support statement, or explicit reader action. Do not choose a health verdict. `pageFraming` is only a visible-page observation: historical_record when the quote itself records a named completed past context; current_guidance when it presents guidance, status, availability, or a rule for a reader now; mixed or neutral otherwise. A past date does not make current wording historical: "currently ... until 2025" is current_guidance with timeAnchorText="until 2025". A recap's old prediction or plan remains historical_record. For reader_action, kind=reader_action and use a permitted actionType; otherwise kind=factual_statement and actionType=null. Exclude generic footer/navigation/social CTAs and rhetorical invitations.
        subject names the exact event, programme, application, offer, vacancy, procedure, product, or rule. aspect names the exact condition or proposition. jurisdiction names the governing country, authority, product ecosystem, or organisation when relevant; otherwise use an empty string. timeReference copies only a year, date, edition, deadline, or period visible in the quote and relevant to that observation; otherwise null. Never use publication date, modified date, author date, URL year, or other editorial metadata as timeReference.
        pageQuote is one exact contiguous substring from pageContent. Do not paraphrase, join locations, or use ellipses. Preserve official names and dates. timeAnchorText is the exact visible date/period/status phrase or null. timeAnchorEndDate is ISO only when the page explicitly establishes a date; otherwise null. researchQuestion asks whether the exact assertion was true in its historical period or is valid now according to pageFraming.

        Return only JSON matching the schema.
        """;

    private const string IntrinsicAuditSystemPrompt = """
        You are a senior web content auditor. Diagnose only intrinsic issues: defects provable from the visible page snapshot alone. Do not use outside knowledge, dates relative to today, or claims requiring verification.

        The default outcome is no issue: return an empty issues array whenever the page substantively fulfills its purpose and no issue definition is directly proven. Do not infer a defect from an alternative editorial preference, a possible ambiguity, or information that would merely be nice to add. Emit an issue only when visible page evidence directly satisfies that issue's definition.
        pageIntent, purpose, structuralInventory, and readerActions are factual context extracted from that same snapshot. Use them to orient the audit, but do not treat them as a health verdict. In particular, a form with a submit control, a relevant contact method, or a relevant CTA in structuralInventory is a usable next step even when it appears before the final section. Do not infer that a transactional next step is missing solely because the final CTA is text-only. Before emitting missing_expected_next_step, inspect readerActions: when one represents a visible action relevant to the page's central purpose, the page has a usable next step and this issue must not be emitted.

        For issues, use only this taxonomy:
        - insufficient_information: too little usable main content to assess fulfillment of the page's purpose; not merely missing optional detail or public prices.
        - unclear_messaging: a materially confusing, vague, disorganized, or difficult-to-follow main message; not minor style issues.
        - within_page_inconsistency: two visible material statements contradict each other; not a conflict requiring outside knowledge.
        - title_body_mismatch: title or major heading makes a specific material promise that the main body does not substantively address or directly contradicts. Conditions, exceptions, transitions, or case-by-case limitations do not constitute a mismatch when the body explains them.
        - url_content_mismatch: a semantically specific URL promise is not delivered by main content; not a legacy, broad, or branded URL.
        - missing_trust_context: necessary author, source, jurisdiction, date, method, attribution, or scope is absent and materially limits reliance. Do not emit this issue merely because an informational or legal page lacks external citations when it visibly identifies a qualified author or reviewer and states the relevant jurisdiction or legal basis.
        - missing_expected_next_step: a transactional page has no visible, usable next step for its central action. Evaluate the whole main content, not only the final CTA or the end of the page. A labeled CTA, contact method, booking flow, application form, or form with a submit action anywhere in the central conversion content is a clear next step. For recruitment, an explicit invitation for candidates to contact the organization or visit a careers page is also a usable next step. Do not require details about what happens after submission or require the control to appear after the final CTA. Never use for informational or historical pages.
        - brand_safety_risk: visible main content creates a material reputational, policy, or advertiser-suitability risk.
        - sensitive_topic_without_context: a sensitive subject lacks material care, context, framing, or scope needed for its purpose.
        - language_mismatch: substantial main reader-facing content conflicts with declared language, locale route, or language version. Ignore navigation, footer, names, short quotes, and widgets.
        - misleading_current_status: pageContent itself proves something ended, replaced, or unavailable while another visible part promotes it as current.

        Do not emit expired_date_or_deadline or any issue requiring external state. Do not extract claims.
        Emit one issue per distinct underlying defect. When multiple page quotes prove the same defect, return one issue with every material proof in pageQuotes; do not create duplicate issues only because there is another supporting quote.
        Every pageQuotes item must be an exact contiguous page substring that directly proves the issue definition.

        Return only JSON matching the schema.
        """;

    private const string AssertionCoverageSystemPrompt = """
        Review coverage only. Compare the existing assertions with pageContent and return missingAssertions containing only distinct, material, externally checkable rules, deadlines, rates, eligibility conditions, availability statements, exemptions, or explicit reader actions that are absent. Do not repeat, edit, rank, or remove existing assertions. Every pageQuote must be an exact contiguous substring of pageContent. Return JSON only.
        """;

    private const string OpenAiWebEvidenceEvaluationSystemPrompt = """
        Research and evaluate the supplied assertions using Web Search. Return only JSON matching the schema.
        Every non-inconclusive verdict must contain one or two literal quotes from cited Web Search sources, and every sourceUrl must be a URL returned by Web Search in this response. Never use recalled knowledge or invent URLs. For every evidence item, set sourceRole to source_of_record only for the competent public authority, regulator, official programme owner, original data publisher, event organiser, manufacturer, or exact first-party action destination; use independent_secondary only for genuinely independent reporting; otherwise unknown. Prefer source_of_record. If such evidence is unavailable, return not_enough_evidence.
        Resolution must match assertion kind: historical_statement -> true_in_context, false_in_context, or not_enough_evidence; current_guidance -> applicable, unavailable, changed, or not_enough_evidence; reader_action -> available, unavailable, changed, or not_enough_evidence. A historical statement is evaluated only in its stated period. Current guidance and reader actions are evaluated at analysisDate. identityMatch must be exact for a conclusive verdict.
        For every current_guidance or reader_action, explicitly investigate the asserted period, deadline, edition, rate, eligibility condition, or status against analysisDate. If the page presents a benefit, rule, action, or exemption as current but names an end period before analysisDate, search the responsible source for what happened after that period. When the official source says that the named arrangement ended, was withdrawn, expired, or was replaced, return unavailable or changed and set temporalEvidence.status=explicitly_ended with the literal ending/replacement quote. When it explicitly says the arrangement remains in force, return applicable or available and set status=explicitly_current. Do not treat a different general benefit as proof that the exact asserted benefit remains available. A source establishing only a previous period is historical_only and requires not_enough_evidence for current guidance or reader action. A later edition, cycle, version, or deadline can support changed or unavailable only when it concerns the exact same subject, aspect, and jurisdiction. Generic contact pages do not prove that a specific legal, application, or programme window is available.
        """;

    private const string EvidenceEvaluationSystemPrompt = """
        Evaluate assertions only against the supplied Tavily evidence packet. Do not browse, use outside knowledge, invent a source, or cite a URL absent from the packet.
        Only evaluate packet entries with the same claimId as the assertion. Each entry is one independently addressable chunk. Choose quotes wholly inside one chunk; never quote across chunks or include the `[...]` separator.
        Resolution must match assertion kind: historical_statement -> true_in_context, false_in_context, or not_enough_evidence; current_guidance -> applicable, unavailable, changed, or not_enough_evidence; reader_action -> available, unavailable, changed, or not_enough_evidence. A historical statement is evaluated only in its stated period; never call it unavailable or outdated merely because that period has passed. Current guidance and reader actions are evaluated only for present applicability or availability at analysisDate. For every check, return temporalEvidence: it must cite one literal quote inside one supplied packet chunk. Its status is explicitly_current only when that quote directly establishes present status; explicitly_ended only when it directly establishes that the action/guidance ended or was replaced; historical_only for an earlier period; unknown otherwise. effectiveEndDate is an ISO date only when the quoted source establishes an end date; otherwise null. Current guidance or a reader action with historical_only or unknown requires not_enough_evidence. A source with an effectiveEndDate before analysisDate cannot support applicable or available. For every check, return evidenceScope extracted from the supplied evidence, identityMatch, and temporalRelation. identityMatch is exact only when subject, aspect, and jurisdiction match the assertion; otherwise partial or mismatch. A conclusive verdict requires identityMatch=exact. temporalRelation is same, later, earlier, different, or unknown relative to assertion.timeReference. A later edition, cycle, version, or deadline can still have identityMatch=exact: use unavailable when the old action cannot be taken, or changed when it remains possible only under materially different terms. A different legal route, product tier, or jurisdiction has identityMatch other than exact and must return not_enough_evidence. A generic contact page or appointment page does not prove a legal/application window is available. Return up to two evidence items.
        """;

    private const string AuthorityRoutingSystemPrompt = """
        You route assertions to curated authority collections. Select a collection only when the assertion's visible subject, jurisdiction, and topic clearly fit its description and the collection can plausibly contain primary evidence. Do not infer a jurisdiction from the page language alone. You may select more than one collection when both are directly relevant, such as a Spanish legal rule about nationality. When no collection clearly fits, return an empty collectionIds array. Do not select URLs or issue verdicts. Return only JSON matching the schema.
        """;

    private const string EvidenceSourceSelectionSystemPrompt = """
        Select up to two eligible candidates per claim for targeted extraction. A selected candidate must be source_of_record (issuing authority, regulator, organiser, manufacturer, company, or original data publisher), or independent_secondary for a historical statement only when no source of record is available. Never select an unknown or ineligible source. A candidate with chunkId `direct-action-destination` is only a candidate: select it only when its URL plausibly belongs to the organisation or system that controls that exact action. Do not select commentary, law firms, consultancies, resellers, SEO pages, news summaries, or aggregators merely because they agree with the claim. If no candidate qualifies, return an empty candidates array.
        Return only JSON matching the schema.
        """;

    private const string AuthorityRoutingSchemaJson = """
        {"type":"object","additionalProperties":false,"required":["routes"],"properties":{"routes":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["assertionId","collectionIds","reason"],"properties":{"assertionId":{"type":"string"},"collectionIds":{"type":"array","maxItems":3,"items":{"type":"string"}},"reason":{"type":"string"}}}}}}
        """;

    private const string UnderstandingSchemaJson = """
        {"type":"object","additionalProperties":false,"required":["purpose","pageSummary","intent","assertions"],"properties":{
          "purpose":{"type":"object","additionalProperties":false,"required":["type","rationale"],"properties":{"type":{"type":"string"},"rationale":{"type":"string"}}},
          "pageSummary":{"type":"string"},
          "intent":{"type":"string","enum":["article","referral_stub","hub_index","pricing","product","contact","landing","faq","legal","support_doc","profile_bio","composite_page","announcement","event_coverage","job_posting","unknown"]},
          "assertions":{"type":"array","maxItems":10,"items":{"type":"object","additionalProperties":false,"required":["id","kind","actionType","subject","aspect","pageFraming","timeAnchorText","timeAnchorEndDate","jurisdiction","pageQuote","whyMaterial","researchQuestion","sourceQuery","targetUrl"],"properties":{"id":{"type":"string"},"kind":{"type":"string","enum":["factual_statement","reader_action"]},"actionType":{"type":["string","null"],"enum":["application","registration","booking","purchase","submission","scheduling","consultation","recruitment",null]},"subject":{"type":"string"},"aspect":{"type":"string"},"pageFraming":{"type":"string","enum":["historical_record","current_guidance","mixed","neutral"]},"timeAnchorText":{"type":["string","null"]},"timeAnchorEndDate":{"type":["string","null"]},"jurisdiction":{"type":"string"},"pageQuote":{"type":"string"},"whyMaterial":{"type":"string"},"researchQuestion":{"type":"string"},"sourceQuery":{"type":"string"},"targetUrl":{"type":["string","null"]}}}}
        }}
        """;

    private const string AssertionCoverageSchemaJson = """
        {"type":"object","additionalProperties":false,"required":["missingAssertions"],"properties":{"missingAssertions":{"type":"array","maxItems":5,"items":{"type":"object","additionalProperties":false,"required":["id","kind","actionType","subject","aspect","pageFraming","timeAnchorText","timeAnchorEndDate","jurisdiction","pageQuote","whyMaterial","researchQuestion","sourceQuery","targetUrl"],"properties":{"id":{"type":"string"},"kind":{"type":"string","enum":["factual_statement","reader_action"]},"actionType":{"type":["string","null"],"enum":["application","registration","booking","purchase","submission","scheduling","consultation","recruitment",null]},"subject":{"type":"string"},"aspect":{"type":"string"},"pageFraming":{"type":"string","enum":["historical_record","current_guidance","mixed","neutral"]},"timeAnchorText":{"type":["string","null"]},"timeAnchorEndDate":{"type":["string","null"]},"jurisdiction":{"type":"string"},"pageQuote":{"type":"string"},"whyMaterial":{"type":"string"},"researchQuestion":{"type":"string"},"sourceQuery":{"type":"string"},"targetUrl":{"type":["string","null"]}}}}}}
        """;

    private const string IntrinsicAuditSchemaJson = """
        {"type":"object","additionalProperties":false,"required":["issues"],"properties":{
          "issues":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["code","reason","pageQuotes"],"properties":{"code":{"type":"string","enum":["insufficient_information","unclear_messaging","within_page_inconsistency","title_body_mismatch","url_content_mismatch","missing_trust_context","missing_expected_next_step","brand_safety_risk","sensitive_topic_without_context","language_mismatch","misleading_current_status"]},"reason":{"type":"string"},"pageQuotes":{"type":"array","minItems":1,"items":{"type":"string"}}}}}
        }}
        """;

    private const string EvidenceEvaluationSchemaJson = """
        {"type":"object","additionalProperties":false,"required":["checks"],"properties":{"checks":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["claimId","verdict","reason","currentFact","identityMatch","temporalRelation","temporalEvidence","evidenceScope","evidence"],"properties":{"claimId":{"type":"string"},"verdict":{"type":"string","enum":["true_in_context","false_in_context","applicable","available","unavailable","changed","not_enough_evidence"]},"reason":{"type":"string"},"currentFact":{"type":["string","null"]},"identityMatch":{"type":"string","enum":["exact","partial","mismatch"]},"temporalRelation":{"type":"string","enum":["same","later","earlier","different","unknown"]},"temporalEvidence":{"type":"object","additionalProperties":false,"required":["sourceUrl","chunkId","quote","status","effectiveEndDate"],"properties":{"sourceUrl":{"type":["string","null"]},"chunkId":{"type":["string","null"]},"quote":{"type":["string","null"]},"status":{"type":"string","enum":["explicitly_current","explicitly_ended","historical_only","unknown"]},"effectiveEndDate":{"type":["string","null"]}}},"evidenceScope":{"type":"object","additionalProperties":false,"required":["subject","aspect","timeReference","jurisdiction"],"properties":{"subject":{"type":"string"},"aspect":{"type":"string"},"timeReference":{"type":["string","null"]},"jurisdiction":{"type":"string"}}},"evidence":{"type":"array","maxItems":2,"items":{"type":"object","additionalProperties":false,"required":["sourceUrl","chunkId","quote"],"properties":{"sourceUrl":{"type":"string"},"chunkId":{"type":"string"},"quote":{"type":"string"}}}}}}}}}
        """;

    private const string EvidenceSourceSelectionSchemaJson = """
        {"type":"object","additionalProperties":false,"required":["selections"],"properties":{"selections":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["claimId","candidates"],"properties":{"claimId":{"type":"string"},"candidates":{"type":"array","maxItems":2,"items":{"type":"object","additionalProperties":false,"required":["url","sourceClass"],"properties":{"url":{"type":"string"},"sourceClass":{"type":"string","enum":["source_of_record","independent_secondary"]}}}}}}}}}
        """;
}

internal sealed record FactCheckPipelineInput(
    string Url,
    string FinalUrl,
    string Title,
    string PageLanguage,
    string ContentDate,
    string PageContent,
    SecondaryStructuralInventory StructuralInventory,
    string OutputLanguage,
    DateOnly AnalysisDate,
    bool EnableExternalVerification);

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

internal sealed record FactCheckPipelineExecution(
    JsonElement Analysis,
    string Model,
    string RawResponse,
    string[] SourceUrls,
    string? Error);

internal sealed record SecondaryClaimToVerify(
    string Id,
    string Kind,
    string Subject,
    string Aspect,
    string? TimeReference,
    string Jurisdiction,
    string PageQuote,
    string WhyMaterial,
    string ResearchQuestion,
    string SourceQuery,
    string? DirectSourceUrl);

internal sealed record SecondaryEvidenceSource(string ClaimId, string Url, string Title, string Content, string ChunkId, string SourceClass, string EvidenceMode);

internal sealed record SecondaryCitedEvidence(string Url, string ChunkId, string Quote, string SourceClass, string EvidenceMode);

internal sealed record SecondaryTemporalEvidence(
    string Url,
    string ChunkId,
    string Quote,
    string Status,
    DateOnly? EffectiveEndDate);

internal sealed record SecondaryEvidenceRetrieval(
    IReadOnlyList<SecondaryEvidenceSource> Sources,
    IReadOnlyList<SecondaryRetrievalTrace> Trace,
    int BasicSearchRequests = 0);

internal sealed record SecondaryRetrievalUsage(
    int BasicSearchRequests,
    int UniqueUrlsSelected,
    string Provider = "tavily",
    int OpenAiWebSearchCalls = 0)
{
    // Tavily Basic Extract is billed per five successful URLs. This is an estimate
    // because a provider-side extraction failure can reduce the billable URL count.
    public int BasicExtractRequestsEstimate => (UniqueUrlsSelected + 4) / 5;
    public int EstimatedTavilyCredits => BasicSearchRequests + BasicExtractRequestsEstimate;
}

internal sealed record SecondaryRetrievalTrace(
    string ClaimId,
    string Query,
    string Url,
    string Title,
    string Disposition,
    string? Reason);

internal sealed record SecondaryOpenAiWebEvidenceResult(
    string EvaluationJson,
    IReadOnlyList<SecondaryEvidenceSource> Evidence,
    IReadOnlyList<SecondaryRetrievalTrace> Trace,
    int WebSearchCalls,
    string RawResponse);

internal sealed record SecondaryVerificationResult(
    SecondaryClaimToVerify Claim,
    string Verdict,
    string Reason,
    string? CurrentFact,
    string? SourceUrl,
    string? EvidenceQuote,
    string[] SourceUrls,
    string SourceRole,
    string SourceEligibility,
    string EvidenceMode)
{
    public static SecondaryVerificationResult NotEnoughEvidence(SecondaryClaimToVerify claim, string reason, string[]? sourceUrls = null) =>
        new(claim, "not_enough_evidence", reason, null, null, null, sourceUrls ?? [], "unknown", "rejected", "inconclusive");
}
