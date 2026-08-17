using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using Microsoft.Playwright;

namespace VersaCore;

internal static partial class Program
{
    private static readonly bool EnvironmentLoaded = LoadEnvironment();
    private static string? FactCheckModelOverride;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HttpClient OpenAiHttpClient = CreateOpenAiHttpClient();
    private static readonly HttpClient AnthropicHttpClient = CreateAnthropicHttpClient();
    private static readonly HttpClient TavilyHttpClient = CreateTavilyHttpClient();
    private static readonly HttpClient SourceValidationHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private static readonly Regex EmailRegex = new(
        @"\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PhoneRegex = new(
        @"(?<!\w)(?:\+?\d[\d\s().\-]{7,}\d)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AddressLikeRegex = new(
        @"(?:\b\d{4,6}\b.*\b[\p{L}][\p{L}\-]+\b|\b(?:street|st\.?|avenue|ave\.?|road|rd\.?|drive|dr\.?|boulevard|blvd|plaza|square|calle|avenida|avda|camino|paseo|carrer|rua)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] NoiseSelectors =
    [
        "script",
        "style",
        "noscript",
        "iframe",
        "svg",
        "canvas",
        ".cookie",
        ".cookies",
        ".popup",
        ".advertisement",
        ".social-share",
        ".grecaptcha-badge",
        "access-widget-ui"
    ];

    private static bool LoadEnvironment()
    {
        DotNetEnv.Env.TraversePath().Load();
        return true;
    }

    public static async Task<int> Main(string[] args)
    {
        var useSecondaryPipeline = args.Length > 0
            && string.Equals(args[0], "--secondary-pipeline", StringComparison.Ordinal);
        if (useSecondaryPipeline) args = args[1..];

        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet run --project C:\\Dev\\versa\\versa-core -- [--secondary-pipeline] <url> [outputLanguage]");
            return 1;
        }

        if (string.Equals(args[0], "--fact-check-replay", StringComparison.Ordinal))
        {
            return await ReplayFactCheckAsync(args);
        }

        var url = args[0].Trim();
        var outputLanguage = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]) ? args[1].Trim() : "en";


        if (!Uri.TryCreate(url, UriKind.Absolute, out var pageUri)
            || (pageUri.Scheme != Uri.UriSchemeHttp && pageUri.Scheme != Uri.UriSchemeHttps))
        {
            Console.Error.WriteLine("URL must be an absolute http/https URL.");
            return 1;
        }

        // Create output directory
        var outputDir = CreateOutputDirectory(pageUri);
        Directory.CreateDirectory(outputDir);

        try
        {
            //1º step: capture with Playwright
            var capture = await CaptureAsync(url);
            await File.WriteAllTextAsync(Path.Combine(outputDir, "captured-body.txt"), capture.BodyText);

            if (!string.IsNullOrWhiteSpace(capture.UnsupportedContentType))
            {
                var unsupportedResult = new PipelineResult
                {
                    Url = url,
                    FinalUrl = capture.FinalUrl ?? url,
                    OutputLanguage = outputLanguage,
                    ExecutedAt = DateTimeOffset.UtcNow,
                    Capture = capture,
                    Llm = new LlmResultMetadata
                    {
                        Provider = AppConstants.Provider,
                        Error = "unsupported_content_type"
                    }
                };
                await File.WriteAllTextAsync(
                    Path.Combine(outputDir, "result.json"),
                    JsonSerializer.Serialize(unsupportedResult, JsonOptions));
                Console.WriteLine(JsonSerializer.Serialize(unsupportedResult, JsonOptions));
                Console.Error.WriteLine($"Artifacts saved to: {outputDir}");
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(capture.BlockedReason))
            {
                var blockedResult = new PipelineResult
                {
                    Url = url,
                    FinalUrl = capture.FinalUrl ?? url,
                    OutputLanguage = outputLanguage,
                    ExecutedAt = DateTimeOffset.UtcNow,
                    Capture = capture,
                    Llm = new LlmResultMetadata
                    {
                        Provider = AppConstants.Provider,
                        Error = "capture_blocked"
                    }
                };
                await File.WriteAllTextAsync(
                    Path.Combine(outputDir, "result.json"),
                    JsonSerializer.Serialize(blockedResult, JsonOptions));
                Console.WriteLine(JsonSerializer.Serialize(blockedResult, JsonOptions));
                Console.Error.WriteLine($"Artifacts saved to: {outputDir}");
                return 0;
            }

            var document = await ParseDocumentAsync(capture.Html);

            //2º step: extract metadata
            var metadata = await ExtractMetadata(document, capture.FinalUrl ?? url);
            var structuralOutline = await BuildStructuralOutlineAsync(document);
            await File.WriteAllTextAsync(
                Path.Combine(outputDir, "structural-outline.md"),
                structuralOutline.Markdown);

            //3º step: use structural outline as the single extraction artifact
            await File.WriteAllTextAsync(Path.Combine(outputDir, "final-clean.txt"), structuralOutline.Markdown);

            //4º step: analyze with LLM
            LlmOutput? llm = null;
            if (!string.IsNullOrWhiteSpace(AppConstants.ApiKey)
                && (useSecondaryPipeline ? HasConfiguredSecondaryModels() : HasConfiguredOpenAiModels()))
            {
                try
                {
                    llm = useSecondaryPipeline
                        ? await ExecuteSecondaryPipelineAsync(url, capture.FinalUrl ?? url, metadata, structuralOutline, outputLanguage)
                        : await AnalyzeWithLlmAsync(url, capture.FinalUrl ?? url, metadata, capture, structuralOutline, outputLanguage);
                }
                catch (Exception ex)
                {
                    llm = new LlmOutput
                    {
                        Provider = AppConstants.Provider,
                        Model = GetStageModel(LlmStage.Diagnosis),
                        Error = ex.ToString()
                    };
                }
            }

            //5th step: compute content quality score
            ScoringResult? scoring = null;
            if (!useSecondaryPipeline && llm?.ParsedJson is not null)
            {
                scoring = ComputeContentQualityScore(llm.ParsedJson.Value, capture, metadata);
            }

            // Final result aggregation
            var result = new PipelineResult
            {
                Url = url,
                FinalUrl = capture.FinalUrl ?? url,
                OutputLanguage = outputLanguage,
                ExecutedAt = DateTimeOffset.UtcNow,
                Capture = capture,
                Metadata = metadata,
                StructuralOutline = structuralOutline,
                Extraction = new ExtractionResult
                {
                    Method = "structural-outline",
                    EstimatedTokens = structuralOutline.Summary.EstimatedTokens,
                    TruncatedByTextLimit = structuralOutline.Summary.TruncatedByTextLimit
                },
                Analysis = llm?.ParsedJson,
                Llm = llm is null
                    ? null
                    : new LlmResultMetadata
                    {
                        Provider = llm.Provider,
                        Model = llm.Model,
                        Error = llm.Error,
                        WebSourceUrls = llm.WebSourceUrls
                    },
                Scoring = scoring
            };

            var resultPath = Path.Combine(outputDir, "result.json");
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, JsonOptions));
            if (llm?.ParsedJson is not null)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(outputDir, "analysis.json"),
                    llm.ParsedJson.Value.GetRawText());
            }

            if (llm is not null)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(outputDir, "llm-debug.json"),
                    JsonSerializer.Serialize(
                        new
                        {
                            provider = llm.Provider,
                            model = llm.Model,
                            rawResponse = llm.RawResponse,
                            parsedResponse = llm.ParsedResponse,
                            error = llm.Error,
                            webSourceUrls = llm.WebSourceUrls
                        },
                        JsonOptions));
            }

            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            Console.Error.WriteLine($"Artifacts saved to: {outputDir}");
            return 0;
        }
        catch (Exception ex)
        {
            var error = new
            {
                url,
                error = ex.ToString()
            };

            await File.WriteAllTextAsync(
                Path.Combine(outputDir, "result.json"),
                JsonSerializer.Serialize(error, JsonOptions));

            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    // Program is only the CLI/composition boundary for the secondary pipeline.
    // Its analysis, evidence, and resolution contracts live in SecondaryPipeline.cs.
    private static async Task<LlmOutput> ExecuteSecondaryPipelineAsync(
        string url,
        string finalUrl,
        MetadataResult metadata,
        StructuralOutlineResult outline,
        string outputLanguage)
    {
        var execution = await new SecondaryPipeline().RunAsync(new SecondaryPipelineInput(
            url,
            finalUrl,
            metadata.Title,
            metadata.Language,
            metadata.ModifiedDate ?? metadata.PublishedDate,
            outline.Markdown,
            SecondaryStructuralInventory.FromOutline(outline.Markdown),
            outputLanguage,
            DateOnly.FromDateTime(DateTime.UtcNow)));

        return new LlmOutput
        {
            Provider = AppConstants.Provider,
            Model = execution.Model,
            RawResponse = execution.RawResponse,
            ParsedResponse = execution.Analysis.GetRawText(),
            ParsedJson = execution.Analysis,
            Error = execution.Error,
            WebSourceUrls = execution.SourceUrls
        };
    }

    private static async Task<int> ReplayFactCheckAsync(string[] args)
    {
        if (args.Length is < 3 or > 3)
        {
            Console.Error.WriteLine(
                "Usage: --fact-check-replay <result.json> <gpt-5.4-mini|gpt-5.4-nano>");
            return 1;
        }

        var requestedModel = args[2].Trim();
        if (requestedModel is not ("gpt-5.4-mini" or "gpt-5.4-nano"))
        {
            Console.Error.WriteLine("Fact-check replay only permits gpt-5.4-mini or gpt-5.4-nano.");
            return 1;
        }

        var replayPath = Path.GetFullPath(args[1]);
        if (!File.Exists(replayPath))
        {
            Console.Error.WriteLine($"Replay input does not exist: {replayPath}");
            return 1;
        }

        var result = JsonSerializer.Deserialize<PipelineResult>(
            await File.ReadAllTextAsync(replayPath),
            JsonOptions);
        if (result?.Analysis is null)
        {
            Console.Error.WriteLine("Replay input does not contain analysis.");
            return 1;
        }

        var claimLedger = ReadClaimLedger(result.Analysis.Value);
        if (claimLedger.Count == 0)
        {
            Console.Error.WriteLine("Replay input does not contain verification requests.");
            return 1;
        }

        FactCheckModelOverride = requestedModel;
        try
        {
            var batches = new List<LlmOutput>();
            foreach (var batch in claimLedger.Chunk(3))
            {
                var stage = await ExecuteLlmStageAsync(
                    LlmStage.FactCheck,
                    FactCheckSystemPrompt,
                    BuildFactCheckPrompt(result.Url, result.Metadata, result.OutputLanguage, batch),
                    FactCheckResponseSchemaName,
                    FactCheckResponseSchemaJson);
                if (!string.IsNullOrWhiteSpace(stage.Error) || stage.ParsedJson is null)
                {
                    Console.Error.WriteLine(stage.Error ?? "Fact-check replay did not return valid JSON.");
                    return 1;
                }

                var validated = ValidateFactCheckBatch(stage, batch);
                batches.Add(validated);
            }

            var merged = MergeFactCheckBatches(batches);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                replaySource = replayPath,
                model = requestedModel,
                factCheck = merged.ParsedJson,
                webSourceUrls = merged.WebSourceUrls
            }, JsonOptions));
            return 0;
        }
        finally
        {
            FactCheckModelOverride = null;
        }
    }

    private static async Task<CaptureResult> CaptureAsync(string url)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = ResolveChromiumExecutablePath(),
            Args =
            [
                "--no-sandbox",
                "--disable-dev-shm-usage",
                "--disable-crash-reporter"
            ]
        });

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = AppConstants.BrowserUserAgent
        });

        var page = await context.NewPageAsync();
        var totalTimer = Stopwatch.StartNew();

        var navigationTimer = Stopwatch.StartNew();
        var response = await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = AppConstants.NavigationTimeoutMs
        });
        navigationTimer.Stop();

        var contentType = response?.Headers.TryGetValue("content-type", out var responseContentType) == true
            ? responseContentType
            : null;
        if (contentType?.Contains("application/pdf", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new CaptureResult
            {
                StatusCode = response?.Status,
                FinalUrl = page.Url,
                ContentType = contentType,
                UnsupportedContentType = "application/pdf"
            };
        }

        var hydrationTimer = Stopwatch.StartNew();
        await page.WaitForTimeoutAsync(AppConstants.PostDomContentLoadedDelayMs);
        hydrationTimer.Stop();

        var scrollTimer = Stopwatch.StartNew();
        var previousWordCount = await GetRenderedWordCountAsync(page);
        for (var pass = 0; pass < AppConstants.ScrollPasses; pass++)
        {
            await page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)");
            await page.WaitForTimeoutAsync(AppConstants.ScrollDelayMs);

            var currentWordCount = await GetRenderedWordCountAsync(page);
            if (currentWordCount <= previousWordCount + 10)
            {
                break;
            }

            previousWordCount = currentWordCount;
        }
        await page.EvaluateAsync("window.scrollTo(0, 0)");
        scrollTimer.Stop();

        var extractionTimer = Stopwatch.StartNew();
        var bodyText = await page.EvaluateAsync<string>(
            "() => {" +
            "const text = document.body ? (document.body.innerText || document.body.textContent || '') : '';" +
            "return (text || '').replace(/\\s+/g, ' ').trim();" +
            "}");
        var html = await page.ContentAsync();
        extractionTimer.Stop();
        totalTimer.Stop();

        var finalUrl = page.Url;

        return new CaptureResult
        {
            StatusCode = response?.Status,
            FinalUrl = finalUrl,
            ContentType = contentType,
            BlockedReason = DetectCaptureBlock(response?.Status, finalUrl),
            Html = html,
            BodyText = bodyText,
            BodyWordCount = CountWords(bodyText),
            H1Headings = await ExtractHeadingsAsync(page, "h1"),
            H2Headings = await ExtractHeadingsAsync(page, "h2"),
            H3Headings = await ExtractHeadingsAsync(page, "h3")
        };
    }

    private static string? DetectCaptureBlock(int? statusCode, string finalUrl)
    {
        if (statusCode is 401 or 403 or 429 or 503)
        {
            return $"http_{statusCode}";
        }

        if (!Uri.TryCreate(finalUrl, UriKind.Absolute, out var uri)) return null;
        var path = uri.AbsolutePath;
        if (statusCode == 202
            && (path.Contains("captcha", StringComparison.OrdinalIgnoreCase)
                || path.Contains("challenge", StringComparison.OrdinalIgnoreCase)))
        {
            return "anti_bot_challenge";
        }

        return null;
    }

    private static async Task<MetadataResult> ExtractMetadata(IDocument document, string finalUrl)
    {
        var pageUri = new Uri(finalUrl);
        var bodyText = NormalizeWhitespace(document.Body?.TextContent);

        return await Task.FromResult(new MetadataResult
        {
            Title = FirstNonEmpty(
                NormalizeWhitespace(document.Title),
                NormalizeWhitespace(document.QuerySelector("h1")?.TextContent)),
            MetaDescription = FirstNonEmpty(
                document.QuerySelector("meta[name='description']")?.GetAttribute("content"),
                document.QuerySelector("meta[property='og:description']")?.GetAttribute("content")),
            CanonicalUrl = document.QuerySelector("link[rel='canonical']")?.GetAttribute("href") ?? string.Empty,
            SiteName = FirstNonEmpty(
                document.QuerySelector("meta[property='og:site_name']")?.GetAttribute("content"),
                ExtractSiteNameFromTitle(document.Title)),
            Language = FirstNonEmpty(
                document.DocumentElement?.GetAttribute("lang"),
                document.QuerySelector("meta[http-equiv='content-language']")?.GetAttribute("content")),
            Author = FirstNonEmpty(
                document.QuerySelector("meta[name='author']")?.GetAttribute("content"),
                document.QuerySelector("meta[property='article:author']")?.GetAttribute("content")),
            PublishedDate = FirstNonEmpty(
                document.QuerySelector("meta[property='article:published_time']")?.GetAttribute("content"),
                document.QuerySelector("time[datetime]")?.GetAttribute("datetime")),
            ModifiedDate = FirstNonEmpty(
                document.QuerySelector("meta[property='article:modified_time']")?.GetAttribute("content"),
                document.QuerySelector("meta[name='last-modified']")?.GetAttribute("content")),
            UrlPatternHint = InferUrlPatternHint(pageUri.AbsolutePath),
            PaywallDetected = Regex.IsMatch(
                bodyText,
                @"\b(?:subscribe to continue|members only|exclusive for subscribers|subscriber-only|assine para continuar|exclusivo para assinantes|somente para assinantes|paywall)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        });
    }

    private static async Task<LlmOutput?> AnalyzeWithLlmAsync(
        string url,
        string finalUrl,
        MetadataResult metadata,
        CaptureResult capture,
        StructuralOutlineResult structuralOutline,
        string outputLanguage)
    {
        // Intent and temporal grounding are one contextual decision. Diagnosis
        // consumes that context without recomputing it.
        var intentStage = await ExecuteLlmStageAsync(
            LlmStage.Intent,
            IntentSystemPrompt,
            BuildIntentPrompt(url, finalUrl, metadata, capture, structuralOutline, outputLanguage),
            IntentResponseSchemaName,
            IntentResponseSchemaJson);
        if (!string.IsNullOrWhiteSpace(intentStage.Error) || intentStage.ParsedJson is null)
        {
            return new LlmOutput
            {
                Provider = intentStage.Provider, Model = intentStage.Model, RawResponse = intentStage.RawResponse,
                ParsedResponse = intentStage.ParsedResponse, ParsedJson = intentStage.ParsedJson,
                Error = intentStage.Error ?? "Editorial context did not return valid JSON."
            };
        }
        var temporalGroundingStage = await ExecuteLlmStageAsync(
            LlmStage.TemporalGrounding,
            TemporalGroundingSystemPrompt,
            BuildTemporalGroundingPrompt(url, finalUrl, metadata, capture, structuralOutline, outputLanguage, intentStage.ParsedJson.Value),
            TemporalGroundingResponseSchemaName,
            TemporalGroundingResponseSchemaJson);
        if (!string.IsNullOrWhiteSpace(temporalGroundingStage.Error) || temporalGroundingStage.ParsedJson is null)
        {
            return new LlmOutput { Provider = temporalGroundingStage.Provider, Model = temporalGroundingStage.Model,
                RawResponse = temporalGroundingStage.RawResponse, ParsedResponse = temporalGroundingStage.ParsedResponse,
                ParsedJson = temporalGroundingStage.ParsedJson, Error = temporalGroundingStage.Error ?? "Temporal-grounding stage did not return valid JSON." };
        }
        temporalGroundingStage = NormalizeTemporalGrounding(temporalGroundingStage, DateOnly.FromDateTime(DateTime.UtcNow));
        var diagnosisStage = await ExecuteLlmStageAsync(
            LlmStage.Diagnosis,
            DiagnosisSystemPrompt,
            BuildDiagnosisPrompt(url, finalUrl, metadata, capture, structuralOutline, outputLanguage,
                intentStage.ParsedJson.Value, temporalGroundingStage.ParsedJson.Value),
            DiagnosisResponseSchemaName,
            DiagnosisResponseSchemaJson);
        if (!string.IsNullOrWhiteSpace(diagnosisStage.Error) || diagnosisStage.ParsedJson is null)
        {
            return new LlmOutput { Provider = diagnosisStage.Provider, Model = diagnosisStage.Model,
                RawResponse = diagnosisStage.RawResponse, ParsedResponse = diagnosisStage.ParsedResponse,
                ParsedJson = diagnosisStage.ParsedJson, Error = diagnosisStage.Error ?? "Diagnosis stage did not return valid JSON." };
        }

        LlmOutput? verificationSelectionStage = null;
        LlmOutput? factCheckStage = null;
        var finalDiagnosisStage = diagnosisStage;
        JsonElement? verificationSource = null;

        if (AppConstants.Provider == "openai")
        {
            verificationSelectionStage = await ExecuteLlmStageAsync(
                LlmStage.VerificationSelection,
                VerificationSelectionSystemPrompt,
                BuildVerificationSelectionPrompt(
                    url,
                    finalUrl,
                    metadata,
                    structuralOutline,
                    outputLanguage,
                    intentStage.ParsedJson.Value,
                    temporalGroundingStage.ParsedJson.Value),
                VerificationSelectionResponseSchemaName,
                VerificationSelectionResponseSchemaJson);

            if (!string.IsNullOrWhiteSpace(verificationSelectionStage.Error)
                || verificationSelectionStage.ParsedJson is null)
            {
                return new LlmOutput
                {
                    Provider = verificationSelectionStage.Provider,
                    Model = verificationSelectionStage.Model,
                    RawResponse = JsonSerializer.Serialize(new
                    {
                        intent = intentStage.RawResponse,
                        editorialAssessment = diagnosisStage.RawResponse,
                        diagnosis = diagnosisStage.RawResponse,
                        verificationSelection = verificationSelectionStage.RawResponse
                    }, JsonOptions),
                    ParsedResponse = diagnosisStage.ParsedResponse,
                    ParsedJson = diagnosisStage.ParsedJson,
                    Error = verificationSelectionStage.Error ?? "Verification-selection stage did not return valid JSON."
                };
            }

            verificationSource = verificationSelectionStage.ParsedJson.Value;
        }

        var claimLedger = SelectVerificationUnits(
            KeepClaimsWithVisibleQuotes(
                verificationSource is null ? [] : ReadClaimLedger(verificationSource.Value),
                structuralOutline.Markdown));

        diagnosisStage = ApplyDiagnosisContracts(
            diagnosisStage,
            intentStage.ParsedJson.Value,
            temporalGroundingStage.ParsedJson.Value,
            claimLedger.Count);

        if (AppConstants.Provider == "openai" && claimLedger.Count > 0)
        {
            var factCheckBatches = new List<LlmOutput>();
            foreach (var batch in claimLedger.Chunk(3))
            {
                var externalEvidence = AppConstants.UseExternalEvidenceSearch
                    ? await RetrieveTavilyEvidenceAsync(batch)
                    : null;
                var batchStage = await ExecuteLlmStageAsync(
                    LlmStage.FactCheck,
                    AppConstants.UseExternalEvidenceSearch
                        ? ExternalEvidenceFactCheckSystemPrompt
                        : FactCheckSystemPrompt,
                    BuildFactCheckPrompt(
                        url,
                        metadata,
                        outputLanguage,
                        batch,
                        externalEvidence?.PromptText),
                    FactCheckResponseSchemaName,
                    FactCheckResponseSchemaJson,
                    externalEvidence?.SourceUrls,
                    externalEvidence?.SourceContents);

                if (!string.IsNullOrWhiteSpace(batchStage.Error) || batchStage.ParsedJson is null)
                {
                    return new LlmOutput
                    {
                        Provider = batchStage.Provider,
                        Model = batchStage.Model,
                        RawResponse = JsonSerializer.Serialize(new
                        {
                            intent = intentStage.RawResponse,
                            editorialAssessment = diagnosisStage.RawResponse,
                            diagnosis = diagnosisStage.RawResponse,
                            verificationSelection = verificationSelectionStage?.RawResponse,
                            factCheck = batchStage.RawResponse
                        }, JsonOptions),
                        ParsedResponse = diagnosisStage.ParsedResponse,
                        ParsedJson = diagnosisStage.ParsedJson,
                        Error = batchStage.Error ?? "Fact-check stage did not return valid JSON."
                    };
                }

                var validatedBatch = ValidateFactCheckBatch(batchStage, batch);
                // Source URLs, source types, and quoted evidence are validated
                // deterministically above. Do not ask a second model to reinterpret
                // a verdict or replace it with a retry search result.
                factCheckBatches.Add(validatedBatch);
            }

            factCheckStage = MergeFactCheckBatches(factCheckBatches);
            var factCheckJson = factCheckStage.ParsedJson
                ?? throw new InvalidOperationException("Merged fact-check stage did not return valid JSON.");
            var resolvedDiagnosis = ResolveDiagnosisFromFactChecks(
                diagnosisStage.ParsedJson.Value,
                claimLedger,
                factCheckJson);

            finalDiagnosisStage = new LlmOutput
            {
                Provider = diagnosisStage.Provider,
                Model = diagnosisStage.Model,
                RawResponse = JsonSerializer.Serialize(new
                {
                    resolver = "deterministic_fact_check_resolution",
                    diagnosis = diagnosisStage.RawResponse,
                    factCheck = factCheckStage.RawResponse
                }, JsonOptions),
                ParsedResponse = JsonSerializer.Serialize(resolvedDiagnosis, JsonOptions),
                ParsedJson = resolvedDiagnosis
            };
        }

        var reconciledDiagnosis = finalDiagnosisStage.ParsedJson.Value;

        LlmOutput? recommendationStage = null;
        if (ReadJsonString(reconciledDiagnosis, "classification", "type") == "unhealthy")
        {
            recommendationStage = await ExecuteLlmStageAsync(
                LlmStage.Recommendation,
                RecommendationSystemPrompt,
                BuildRecommendationPrompt(
                    url,
                    finalUrl,
                    metadata,
                    capture,
                    structuralOutline,
                    outputLanguage,
                    intentStage.ParsedJson.Value,
                    reconciledDiagnosis),
                RecommendationResponseSchemaName,
                RecommendationResponseSchemaJson);
        }

        if (recommendationStage is not null
            && (!string.IsNullOrWhiteSpace(recommendationStage.Error) || recommendationStage.ParsedJson is null))
        {
            return new LlmOutput
            {
                Provider = recommendationStage.Provider,
                Model = recommendationStage.Model,
                RawResponse = JsonSerializer.Serialize(new
                {
                    editorialAssessment = diagnosisStage.RawResponse,
                    diagnosis = finalDiagnosisStage.RawResponse,
                    verificationSelection = verificationSelectionStage?.RawResponse,
                    factCheck = factCheckStage?.RawResponse,
                    recommendation = recommendationStage.RawResponse
                }, JsonOptions),
                ParsedResponse = finalDiagnosisStage.ParsedResponse,
                ParsedJson = finalDiagnosisStage.ParsedJson,
                Error = recommendationStage.Error ?? "Recommendation stage did not return valid JSON."
            };
        }

        var mergedJson = MergeStageOutputs(
            intentStage.ParsedJson.Value,
            reconciledDiagnosis,
            claimLedger,
            factCheckStage?.ParsedJson,
            recommendationStage?.ParsedJson);

        var mergedResponse = JsonSerializer.Serialize(mergedJson, JsonOptions);

        return new LlmOutput
        {
            Provider = finalDiagnosisStage.Provider,
            Model = finalDiagnosisStage.Model,
            RawResponse = JsonSerializer.Serialize(new
            {
                intent = intentStage.RawResponse,
                editorialAssessment = diagnosisStage.RawResponse,
                diagnosis = diagnosisStage.RawResponse,
                verificationSelection = verificationSelectionStage?.RawResponse,
                factCheck = factCheckStage?.RawResponse,
                finalDiagnosis = finalDiagnosisStage.RawResponse,
                recommendation = recommendationStage?.RawResponse
            }, JsonOptions),
            ParsedResponse = mergedResponse,
            ParsedJson = mergedJson,
            Error = ValidateMergedOutput(mergedJson),
            WebSourceUrls = factCheckStage?.WebSourceUrls ?? []
        };
    }

    private static HttpClient CreateOpenAiHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            UseProxy = false
        };

        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(AppConstants.BaseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(60)
        };

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AppConstants.ApiKey);
        return http;
    }

    private static HttpClient CreateAnthropicHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            UseProxy = false
        };

        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(AppConstants.AnthropicBaseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(60)
        };

        http.DefaultRequestHeaders.Add("x-api-key", AppConstants.AnthropicApiKey);
        http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        return http;
    }

    private static HttpClient CreateTavilyHttpClient()
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(AppConstants.TavilyBaseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30)
        };

        if (!string.IsNullOrWhiteSpace(AppConstants.TavilyApiKey))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AppConstants.TavilyApiKey);
        }

        return http;
    }

    private static async Task<LlmOutput> ExecuteLlmStageAsync(
        LlmStage stage,
        string systemPrompt,
        string prompt,
        string? jsonSchemaName,
        string? jsonSchema,
        string[]? suppliedWebSourceUrls = null,
        IReadOnlyDictionary<string, string>? suppliedSourceContents = null)
    {
        if (AppConstants.Provider == "anthropic")
        {
            return await ExecuteAnthropicStageAsync(stage, systemPrompt, prompt);
        }

        if (stage == LlmStage.FactCheck)
        {
            // Supplying retrieved source content is an explicit evidence-only mode.
            // The secondary pipeline uses it with Tavily so the model cannot mix a
            // second, untracked OpenAI web-search result set into the verdict.
            var responsesPayload = BuildOpenAiResponsesPayload(
                stage,
                systemPrompt,
                prompt,
                jsonSchemaName,
                jsonSchema,
                useSuppliedEvidenceOnly: suppliedSourceContents is not null);
            using var responsesResponse = await SendOpenAiRequestWithRetryAsync("responses", responsesPayload);
            var responsesRaw = await responsesResponse.Content.ReadAsStringAsync();

            if (!responsesResponse.IsSuccessStatusCode)
            {
                return new LlmOutput
                {
                    Provider = AppConstants.Provider,
                    Model = GetStageModel(stage),
                    RawResponse = responsesRaw,
                    Error = $"HTTP {(int)responsesResponse.StatusCode}: {responsesRaw}"
                };
            }

            var responsesContent = ExtractOpenAiResponsesContent(responsesRaw);
            if (string.IsNullOrWhiteSpace(responsesContent))
            {
                return new LlmOutput
                {
                    Provider = AppConstants.Provider,
                    Model = GetStageModel(stage),
                    RawResponse = responsesRaw,
                    Error = "OpenAI Responses API response did not contain output text."
                };
            }

            var parsedJson = TryParseJson(responsesContent);
            return new LlmOutput
            {
                Provider = AppConstants.Provider,
                Model = GetStageModel(stage),
                RawResponse = responsesRaw,
                ParsedResponse = responsesContent,
                ParsedJson = parsedJson,
                WebSourceUrls = suppliedWebSourceUrls ?? ExtractOpenAiWebSourceUrls(responsesRaw),
                RetrievedSourceContents = suppliedSourceContents
            };
        }

        var payload = BuildOpenAiPayload(stage, systemPrompt, prompt, jsonSchemaName, jsonSchema);

        using var response = await SendOpenAiRequestWithRetryAsync("chat/completions", payload);
        var raw = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            return new LlmOutput
            {
                Provider = AppConstants.Provider,
                Model = GetStageModel(stage),
                RawResponse = raw,
                Error = $"HTTP {(int)response.StatusCode}: {raw}"
            };
        }

        var content = ExtractOpenAiContent(raw);
        if (string.IsNullOrWhiteSpace(content))
        {
            return new LlmOutput
            {
                Provider = AppConstants.Provider,
                Model = GetStageModel(stage),
                RawResponse = raw,
                Error = "OpenAI response did not contain message content."
            };
        }

        return new LlmOutput
        {
            Provider = AppConstants.Provider,
            Model = GetStageModel(stage),
            RawResponse = raw,
            ParsedResponse = content,
            ParsedJson = TryParseJson(content)
        };
    }

    private static async Task<LlmOutput> ExecuteAnthropicStageAsync(LlmStage stage, string systemPrompt, string prompt)
    {
        var payload = new
        {
            model = GetAnthropicStageModel(stage),
            max_tokens = 2048,
            temperature = 0,
            system = systemPrompt,
            messages = new object[]
            {
                new { role = "user", content = prompt }
            }
        };

        const int maxAttempts = 3;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "messages")
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(payload, JsonOptions),
                        Encoding.UTF8,
                        "application/json")
                };

                using var response = await AnthropicHttpClient.SendAsync(request);
                var raw = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return new LlmOutput
                    {
                        Provider = "anthropic",
                        Model = GetAnthropicStageModel(stage),
                        RawResponse = raw,
                        Error = $"HTTP {(int)response.StatusCode}: {raw}"
                    };

                var content = ExtractAnthropicContent(raw);
                if (string.IsNullOrWhiteSpace(content))
                    return new LlmOutput
                    {
                        Provider = "anthropic",
                        Model = GetAnthropicStageModel(stage),
                        RawResponse = raw,
                        Error = "Anthropic response did not contain text content."
                    };

                return new LlmOutput
                {
                    Provider = "anthropic",
                    Model = GetAnthropicStageModel(stage),
                    RawResponse = raw,
                    ParsedResponse = content,
                    ParsedJson = TryParseJson(content)
                };
            }
            catch (Exception ex) when (ex is HttpRequestException && attempt < maxAttempts)
            {
                lastException = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt));
            }
        }

        throw lastException ?? new InvalidOperationException("Anthropic request failed.");
    }

    private static object BuildOpenAiPayload(
        LlmStage stage,
        string systemPrompt,
        string prompt,
        string? jsonSchemaName,
        string? jsonSchema)
    {
        if (!string.IsNullOrWhiteSpace(jsonSchemaName)
            && !string.IsNullOrWhiteSpace(jsonSchema))
        {
            return new
            {
                model = GetStageModel(stage),
                temperature = 0,
                response_format = new
                {
                    type = "json_schema",
                    json_schema = new
                    {
                        name = jsonSchemaName,
                        strict = true,
                        schema = JsonNode.Parse(jsonSchema)
                    }
                },
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = prompt }
                }
            };
        }

        return new
        {
            model = GetStageModel(stage),
            temperature = 0,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = prompt }
            }
        };
    }

    private static object BuildOpenAiResponsesPayload(
        LlmStage stage,
        string systemPrompt,
        string prompt,
        string? jsonSchemaName,
        string? jsonSchema,
        bool useSuppliedEvidenceOnly = false)
    {
        object text = !string.IsNullOrWhiteSpace(jsonSchemaName) && !string.IsNullOrWhiteSpace(jsonSchema)
            ? new
            {
                format = new
                {
                    type = "json_schema",
                    name = jsonSchemaName,
                    strict = true,
                    schema = JsonNode.Parse(jsonSchema)
                }
            }
            : new
            {
                format = new
                {
                    type = "json_object"
                }
            };

        if (AppConstants.UseExternalEvidenceSearch || useSuppliedEvidenceOnly)
        {
            return new
            {
                model = GetStageModel(stage),
                text,
                input = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = prompt }
                }
            };
        }

        return new
        {
            model = GetStageModel(stage),
            tools = new object[]
            {
                new
                {
                    type = "web_search"
                }
            },
            tool_choice = "required",
            include = new[] { "web_search_call.action.sources" },
            text,
            input = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = prompt }
            }
        };
    }

    private static JsonElement ResolveDiagnosisFromFactChecks(
        JsonElement diagnosis,
        IReadOnlyList<JsonElement> verificationRequests,
        JsonElement factCheck)
    {
        if (!factCheck.TryGetProperty("checks", out var checks)
            || checks.ValueKind != JsonValueKind.Array)
        {
            return diagnosis;
        }

        var materialFindings = new List<(JsonElement Check, JsonElement Request, int Score)>();

        foreach (var check in checks.EnumerateArray())
        {
            var verdict = ReadJsonString(check, "verdict");
            if (verdict is not ("contradicted" or "outdated" or "partially_supported"))
            {
                continue;
            }

            var claimId = ReadJsonString(check, "claimId");
            if (string.IsNullOrWhiteSpace(claimId))
            {
                continue;
            }

            var request = verificationRequests.FirstOrDefault(candidate =>
                string.Equals(ReadJsonString(candidate, "id"), claimId, StringComparison.Ordinal));
            if (request.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!IsMaterialExternalResolutionCandidate(check, request))
            {
                continue;
            }

            var score = ScoreExternalResolutionCandidate(check, request);
            materialFindings.Add((check, request, score));
        }

        if (materialFindings.Count == 0)
        {
            return diagnosis;
        }

        var topFindings = materialFindings
            .OrderByDescending(item => item.Score)
            .Take(3)
            .ToList();

        var contradictedCount = materialFindings.Count(item =>
            string.Equals(ReadJsonString(item.Check, "verdict"), "contradicted", StringComparison.Ordinal));
        var outdatedCount = materialFindings.Count(item =>
            string.Equals(ReadJsonString(item.Check, "verdict"), "outdated", StringComparison.Ordinal));
        var partialCount = materialFindings.Count(item =>
            string.Equals(ReadJsonString(item.Check, "verdict"), "partially_supported", StringComparison.Ordinal));

        var summaryParts = new List<string>();
        if (contradictedCount > 0) summaryParts.Add($"{contradictedCount} contradicted");
        if (outdatedCount > 0) summaryParts.Add($"{outdatedCount} outdated");
        if (partialCount > 0) summaryParts.Add($"{partialCount} partially supported");

        var whyFlaggedParts = new List<string>
        {
            $"External fact-check found material claim issues on this page ({string.Join(", ", summaryParts)})."
        };

        var evidenceSnippets = new JsonArray();
        foreach (var finding in topFindings)
        {
            var claim = ReadJsonString(finding.Request, "claim")
                ?? ReadJsonString(finding.Check, "pageClaim")
                ?? string.Empty;
            var currentFact = ReadJsonString(finding.Check, "currentFact");
            var reason = ReadJsonString(finding.Check, "reason") ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(claim))
            {
                whyFlaggedParts.Add($"The page states: \"{claim}\".");
                evidenceSnippets.Add(new JsonObject
                {
                    ["text"] = claim,
                    ["context"] = string.Empty
                });
            }

            if (!string.IsNullOrWhiteSpace(currentFact))
            {
                whyFlaggedParts.Add(currentFact);
            }

            if (!string.IsNullOrWhiteSpace(reason))
            {
                whyFlaggedParts.Add(reason);
            }
        }

        if (evidenceSnippets.Count == 0)
        {
            evidenceSnippets.Add(new JsonObject
            {
                ["text"] = ReadJsonString(topFindings[0].Check, "pageClaim") ?? "Material claim issue detected.",
                ["context"] = string.Empty
            });
        }

        var node = JsonNode.Parse(diagnosis.GetRawText())!.AsObject();
        var healthIssueCode = topFindings.Any(finding => IsCurrentClaimIssue(finding.Check, finding.Request))
            ? "verified_current_claim_issue"
            : "verified_historical_claim_issue";
        EnsureIssueCode(node, healthIssueCode);
        node["classification"] = new JsonObject
        {
            ["type"] = "unhealthy",
            ["confidence"] = 0.95,
            ["insufficientEvidence"] = false
        };

        var reasons = node["classificationReasons"] as JsonArray ?? new JsonArray();
        if (!ContainsJsonString(reasons, "External fact-check found one or more material claim issues."))
            reasons.Add("External fact-check found one or more material claim issues.");
        if (!ContainsJsonString(reasons, $"Material findings: {string.Join(", ", summaryParts)}."))
            reasons.Add($"Material findings: {string.Join(", ", summaryParts)}.");
        node["classificationReasons"] = reasons;

        node["issue"] = new JsonObject
        {
            ["title"] = "Claim issues detected",
            ["whyFlagged"] = string.Join(" ", whyFlaggedParts.Where(part => !string.IsNullOrWhiteSpace(part))),
            ["evidenceSnippets"] = evidenceSnippets
        };

        using var document = JsonDocument.Parse(node.ToJsonString(JsonOptions));
        return document.RootElement.Clone();
    }

    private static bool IsMaterialExternalResolutionCandidate(JsonElement check, JsonElement request)
    {
        if (!string.Equals(ReadJsonString(check, "evidenceStatus"), "validated", StringComparison.Ordinal))
        {
            return false;
        }

        var factVerdict = ReadJsonString(check, "factVerdict") ?? MapFactVerdict(ReadJsonString(check, "verdict") ?? "unverifiable");
        return factVerdict is "false" or "outdated"
            && (IsCurrentClaimIssue(check, request) || IsHistoricalClaimIssue(check, request));
    }

    private static bool IsCurrentClaimIssue(JsonElement check, JsonElement request) =>
        (ReadJsonString(check, "factVerdict") ?? MapFactVerdict(ReadJsonString(check, "verdict") ?? "unverifiable")) is "false" or "outdated"
        && (ReadJsonBool(request, "presentedAsCurrent") == true
            || ReadJsonString(request, "timeScope") is "current" or "future"
            || ReadJsonString(request, "currentImpact") == "update_required");

    private static bool IsHistoricalClaimIssue(JsonElement check, JsonElement request) =>
        (ReadJsonString(check, "factVerdict") ?? MapFactVerdict(ReadJsonString(check, "verdict") ?? "unverifiable")) == "false"
        && ReadJsonString(request, "timeScope") == "historical";

    private static bool HasFactVerdict(JsonElement diagnosis, string verdict)
    {
        if (!diagnosis.TryGetProperty("factChecks", out var factChecks)
            || factChecks.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var check in factChecks.EnumerateArray())
        {
            var factVerdict = ReadJsonString(check, "factVerdict")
                ?? MapFactVerdict(ReadJsonString(check, "verdict") ?? "unverifiable");
            if (string.Equals(factVerdict, verdict, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static int ScoreExternalResolutionCandidate(JsonElement check, JsonElement request)
    {
        var verdict = ReadJsonString(check, "verdict");
        var presentedAsCurrent = ReadJsonBool(request, "presentedAsCurrent") == true;
        var historicalValidity = ReadJsonString(check, "historicalValidity") ?? "unknown";
        var currentValidity = ReadJsonString(check, "currentValidity") ?? "unknown";
        var currentImpact = ReadJsonString(request, "currentImpact") ?? "context_needed";

        var score = verdict switch
        {
            "contradicted" => 300,
            "outdated" => 200,
            "partially_supported" => 100,
            _ => 0
        };

        if (historicalValidity == "supported" && currentValidity == "outdated")
        {
            score += 80;
            if (verdict == "contradicted" || verdict == "partially_supported")
            {
                score -= 120;
            }
        }

        if (presentedAsCurrent)
        {
            score += 25;
        }

        if (currentImpact == "update_required")
        {
            score += 35;
        }

        return score;
    }

    private static JsonElement MergeStageOutputs(
        JsonElement intent,
        JsonElement diagnosis,
        IReadOnlyList<JsonElement> verificationRequests,
        JsonElement? factCheck,
        JsonElement? recommendation)
    {
        var merged = JsonNode.Parse(diagnosis.GetRawText())!.AsObject();
        if (intent.TryGetProperty("intent", out var intentValue))
        {
            merged["intent"] = JsonNode.Parse(intentValue.GetRawText());
        }

        if (intent.TryGetProperty("pageSummary", out var pageSummaryValue))
        {
            merged["pageSummary"] = JsonNode.Parse(pageSummaryValue.GetRawText());
        }

        NormalizeIssuesAndClassification(merged);

        var normalizedRequests = new JsonArray();
        foreach (var request in verificationRequests)
        {
            normalizedRequests.Add(JsonNode.Parse(request.GetRawText()));
        }
        // claimLedger is the canonical external-claim artifact. Keep the old
        // verificationRequests name during the API migration.
        merged["claimLedger"] = JsonNode.Parse(normalizedRequests.ToJsonString(JsonOptions));
        merged["verificationRequests"] = normalizedRequests;

        var normalizedFactChecks = factCheck is not null
            && factCheck.Value.TryGetProperty("checks", out var factChecksValue)
            ? JsonNode.Parse(factChecksValue.GetRawText())?.AsArray() ?? new JsonArray()
            : new JsonArray();
        merged["factChecks"] = normalizedFactChecks;
        merged["evidenceSummary"] = BuildEvidenceSummary(normalizedFactChecks);

        merged["recommendedAction"] = recommendation is not null
            && recommendation.Value.TryGetProperty("recommendedAction", out var recommendedActionValue)
            ? JsonNode.Parse(recommendedActionValue.GetRawText())
            : null;

        merged["recommendation"] = recommendation is not null
            && recommendation.Value.TryGetProperty("recommendation", out var recommendationValue)
            ? JsonNode.Parse(recommendationValue.GetRawText())
            : null;

        StripIssueSnippetContexts(merged);

        using var document = JsonDocument.Parse(merged.ToJsonString(JsonOptions));
        return document.RootElement.Clone();
    }

    private static JsonObject BuildEvidenceSummary(JsonArray factChecks)
    {
        var statuses = factChecks
            .OfType<JsonObject>()
            .Select(check => check["evidenceStatus"]?.GetValue<string>() ?? "inconclusive")
            .GroupBy(status => status, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return new JsonObject
        {
            ["claimsSubmitted"] = factChecks.Count,
            ["validated"] = statuses.GetValueOrDefault("validated"),
            ["inconclusive"] = statuses.GetValueOrDefault("inconclusive"),
            ["noEvidenceReturned"] = statuses.GetValueOrDefault("no_evidence_returned"),
            ["citationValidationFailed"] = statuses.GetValueOrDefault("citation_validation_failed"),
            ["unsupportedPdfSource"] = statuses.GetValueOrDefault("unsupported_pdf_source")
        };
    }

    private static void StripIssueSnippetContexts(JsonObject merged)
    {
        if (merged["issue"] is not JsonObject issue
            || issue["evidenceSnippets"] is not JsonArray snippets)
        {
            return;
        }

        foreach (var snippet in snippets.OfType<JsonObject>())
        {
            snippet["context"] = string.Empty;
        }
    }

    private static string? ValidateMergedOutput(JsonElement merged)
    {
        if (string.IsNullOrWhiteSpace(ReadJsonString(merged, "pageSummary")))
        {
            return "Merged output does not include pageSummary.";
        }

        var classification = ReadJsonString(merged, "classification", "type") ?? "healthy";
        var issues = ReadIssues(merged);

        if (classification != "healthy"
            && issues.Count == 0)
        {
            return "Merged output is unhealthy but does not include issues.";
        }

        if (classification != "healthy"
            && (string.IsNullOrWhiteSpace(ReadJsonString(merged, "issue", "title"))
                || string.IsNullOrWhiteSpace(ReadJsonString(merged, "issue", "whyFlagged"))))
        {
            return "Merged output does not include complete issue details.";
        }

        if (!merged.TryGetProperty("classificationReasons", out var classificationReasons)
            || classificationReasons.ValueKind != JsonValueKind.Array
            || classificationReasons.GetArrayLength() < 1)
        {
            return "Merged output does not include classification reasons.";
        }

        if (string.IsNullOrWhiteSpace(ReadJsonString(merged, "recommendation", "summary")))
        {
            return "Merged output does not include recommendation summary.";
        }

        var recommendationMode = ReadJsonString(merged, "recommendation", "mode");
        if (recommendationMode is not ("recommendation" or "suggestion"))
        {
            return "Merged output does not include a valid recommendation mode.";
        }

        if (string.IsNullOrWhiteSpace(ReadJsonString(merged, "recommendedAction", "type")))
        {
            return "Merged output does not include recommended action type.";
        }

        var recommendedAction = ReadJsonString(merged, "recommendedAction", "type");
        if (recommendedAction == "no_action_needed" && recommendationMode != "suggestion")
        {
            return "Merged output uses no_action_needed without suggestion mode.";
        }

        if (recommendedAction is not null
            && recommendedAction != "no_action_needed"
            && recommendationMode != "recommendation")
        {
            return "Merged output uses a corrective action without recommendation mode.";
        }

        if (!merged.TryGetProperty("recommendation", out var recommendation)
            || !recommendation.TryGetProperty("steps", out var steps)
            || steps.ValueKind != JsonValueKind.Array
            || steps.GetArrayLength() < 2)
        {
            return "Merged output does not include at least two recommendation steps.";
        }

        return null;
    }

    private static string ExtractAnthropicContent(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (!root.TryGetProperty("content", out var content) || content.GetArrayLength() == 0)
            return string.Empty;

        var first = content[0];
        var text = first.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;

        // Strip markdown code fences if present (```json ... ``` or ``` ... ```)
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
                trimmed = trimmed[(firstNewline + 1)..];
            if (trimmed.EndsWith("```"))
                trimmed = trimmed[..^3].TrimEnd();
        }

        return trimmed;
    }

    private static string ExtractOpenAiResponsesContent(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;

        if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
        {
            return outputText.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();

            foreach (var outputItem in output.EnumerateArray())
            {
                if (!outputItem.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var contentItem in content.EnumerateArray())
                {
                    if (contentItem.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(text.GetString());
                    }
                }
            }

            if (sb.Length > 0)
            {
                return sb.ToString();
            }
        }

        return string.Empty;
    }

    private static string[] ExtractOpenAiWebSourceUrls(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectWebSourceUrls(document.RootElement, urls);
        return urls.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void CollectWebSourceUrls(JsonElement element, HashSet<string> urls)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var type = element.TryGetProperty("type", out var typeValue) && typeValue.ValueKind == JsonValueKind.String
                ? typeValue.GetString()
                : null;

            if (type is "url" or "url_citation"
                && element.TryGetProperty("url", out var urlValue)
                && urlValue.ValueKind == JsonValueKind.String
                && NormalizeSourceUrl(urlValue.GetString()) is { } normalized)
            {
                urls.Add(normalized);
            }

            foreach (var property in element.EnumerateObject())
            {
                CollectWebSourceUrls(property.Value, urls);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectWebSourceUrls(item, urls);
            }
        }
    }

    private static string? NormalizeSourceUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty,
            Host = uri.Host.ToLowerInvariant()
        };
        var normalized = builder.Uri.AbsoluteUri;
        return normalized.EndsWith('/') ? normalized[..^1] : normalized;
    }

    private static async Task<ExternalEvidencePacket> RetrieveTavilyEvidenceAsync(
        IReadOnlyList<JsonElement> verificationRequests)
    {
        if (string.IsNullOrWhiteSpace(AppConstants.TavilyApiKey))
        {
            throw new InvalidOperationException(
                "External evidence search is enabled, but TAVILY_API_KEY is not configured.");
        }

        var sources = new List<ExternalEvidenceSource>();
        foreach (var request in verificationRequests)
        {
            var claimId = ReadJsonString(request, "id") ?? "<unknown>";
            var searchPlan = BuildTavilySearchPlan(request);
            var payload = new
            {
                query = searchPlan.Query,
                search_depth = searchPlan.SearchDepth,
                max_results = searchPlan.MaxResults,
                include_raw_content = "markdown",
                include_answer = false,
                auto_parameters = false
            };

            using var response = await TavilyHttpClient.PostAsync(
                "search",
                new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"));
            var raw = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Tavily search failed for {claimId}: HTTP {(int)response.StatusCode}: {raw}");
            }

            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var result in results.EnumerateArray())
            {
                var sourceUrl = NormalizeSourceUrl(ReadJsonString(result, "url"));
                if (sourceUrl is null)
                {
                    continue;
                }

                var title = ReadJsonString(result, "title") ?? sourceUrl;
                var content = ReadJsonString(result, "raw_content")
                    ?? ReadJsonString(result, "content")
                    ?? string.Empty;
                var excerpt = BuildTavilyEvidenceExcerpt(content, request);
                if (!string.IsNullOrWhiteSpace(excerpt))
                {
                    sources.Add(new ExternalEvidenceSource(
                        claimId,
                        sourceUrl,
                        title,
                        excerpt,
                        NormalizeWhitespace(content)));
                }
            }
        }

        var uniqueSources = sources
            .GroupBy(source => source.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var prompt = new StringBuilder();
        foreach (var source in uniqueSources)
        {
            prompt.AppendLine($"- claimId: {source.ClaimId}");
            prompt.AppendLine($"  title: {source.Title}");
            prompt.AppendLine($"  url: {source.Url}");
            prompt.AppendLine("  retrievedContent:");
            prompt.AppendLine(source.Content);
        }

        return new ExternalEvidencePacket(
            prompt.ToString(),
            uniqueSources.Select(source => source.Url).ToArray(),
            uniqueSources.ToDictionary(source => source.Url, source => source.RetrievedContent, StringComparer.OrdinalIgnoreCase));
    }

    private static TavilySearchPlan BuildTavilySearchPlan(JsonElement request)
    {
        var policy = EvidencePolicies.Resolve(
            EvidencePolicies.ParseClaimType(ReadJsonString(request, "claimType")),
            EvidencePolicies.ParseTimeScope(ReadJsonString(request, "timeScope")),
            EvidencePolicies.ParseRisk(ReadJsonString(request, "risk")));
        // This is an authority requirement, not a domain allow-list. Tavily still discovers
        // the responsible source for the entity, jurisdiction, and claim being checked.
        var parts = new List<string>
        {
            $"official primary {string.Join(" ", policy.PreferredAuthorityKinds.Select(kind => kind.Replace('_', ' ')))} source"
        };
        if (policy.RequiresCurrentSource)
        {
            parts.Add($"current status as of {DateTime.UtcNow:yyyy-MM-dd}");
        }
        parts.Add(ReadJsonString(request, "atomicClaim") ?? string.Empty);
        parts.Add(string.Join(" ", ReadJsonArrayValues(request, "scope")));
        parts.Add(string.Join(" ", ReadJsonArrayValues(request, "context")));

        var requiresAuthoritativeCurrentEvidence = policy.RequiresCurrentSource
            && policy.MinimumSourceTier == MinimumSourceTier.OfficialOrPrimary;
        return new TavilySearchPlan(
            LimitTavilyQueryLength(string.Join(" ", parts.Where(value => !string.IsNullOrWhiteSpace(value)))),
            requiresAuthoritativeCurrentEvidence ? "advanced" : "basic",
            requiresAuthoritativeCurrentEvidence ? 5 : 3);
    }

    private static string LimitTavilyQueryLength(string query)
    {
        const int maxTavilyQueryLength = 400;
        return query.Length <= maxTavilyQueryLength
            ? query
            : query[..maxTavilyQueryLength].TrimEnd();
    }

    private static string BuildTavilyEvidenceExcerpt(string rawContent, JsonElement request)
    {
        var content = NormalizeWhitespace(rawContent);
        const int maxChars = 6000;
        if (content.Length <= maxChars)
        {
            return content;
        }

        var terms = Regex.Matches(
                $"{ReadJsonString(request, "atomicClaim")} {string.Join(" ", ReadJsonArrayValues(request, "scope"))}",
                @"[\p{L}\p{N}]{4,}")
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var paragraphs = rawContent
            .Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeWhitespace)
            .Where(paragraph => paragraph.Length > 40)
            .Select((paragraph, index) => new
            {
                Paragraph = paragraph,
                Index = index,
                Score = terms.Count(term => paragraph.Contains(term, StringComparison.OrdinalIgnoreCase))
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .Take(6)
            .OrderBy(item => item.Index)
            .Select(item => item.Paragraph);

        var excerpt = string.Join("\n\n", paragraphs);
        return excerpt.Length <= maxChars ? excerpt : excerpt[..maxChars];
    }

    private static async Task<HttpResponseMessage> SendOpenAiRequestWithRetryAsync(string relativeUrl, object payload)
    {
        const int maxAttempts = 3;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, relativeUrl)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(payload, JsonOptions),
                        Encoding.UTF8,
                        "application/json")
                };

                return await OpenAiHttpClient.SendAsync(request);
            }
            catch (Exception ex) when (IsTransientOpenAiTransportError(ex) && attempt < maxAttempts)
            {
                lastException = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt));
            }
        }

        throw lastException ?? new InvalidOperationException("OpenAI request failed without a captured exception.");
    }

    private static bool IsTransientOpenAiTransportError(Exception ex)
    {
        if (ex is HttpRequestException)
        {
            return true;
        }

        return ex.InnerException is not null && IsTransientOpenAiTransportError(ex.InnerException);
    }

    private static bool HasConfiguredOpenAiModels() =>
        !string.IsNullOrWhiteSpace(AppConstants.IntentModel)
        && !string.IsNullOrWhiteSpace(AppConstants.DiagnosisModel)
        && !string.IsNullOrWhiteSpace(AppConstants.FactCheckModel)
        && !string.IsNullOrWhiteSpace(AppConstants.RecommendationModel);

    private static bool HasConfiguredSecondaryModels() =>
        !string.IsNullOrWhiteSpace(AppConstants.IntentModel)
        && !string.IsNullOrWhiteSpace(AppConstants.DiagnosisModel)
        && !string.IsNullOrWhiteSpace(AppConstants.FactCheckModel);

    private static string GetStageModel(LlmStage stage) => stage switch
    {
        LlmStage.Intent => AppConstants.IntentModel,
        LlmStage.TemporalGrounding => AppConstants.DiagnosisModel,
        LlmStage.Diagnosis => AppConstants.DiagnosisModel,
        LlmStage.VerificationSelection => AppConstants.VerificationSelectionModel,
        LlmStage.FactCheck or LlmStage.EvidenceEvaluation => FactCheckModelOverride ?? AppConstants.FactCheckModel,
        LlmStage.Recommendation => AppConstants.RecommendationModel,
        _ => AppConstants.DiagnosisModel
    };

    private static string GetAnthropicStageModel(LlmStage stage)
    {
        _ = stage;
        return AppConstants.AnthropicModel;
    }

    private static List<JsonElement> ReadClaimLedger(JsonElement source)
    {
        var hasClaimLedger = source.TryGetProperty("claimLedger", out var requests);
        if (!hasClaimLedger)
        {
            hasClaimLedger = source.TryGetProperty("verificationRequests", out requests);
        }
        if (!hasClaimLedger || requests.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var normalized = new List<JsonElement>();
        var index = 0;
        foreach (var request in requests.EnumerateArray()
                     .Where(request => ReadJsonBool(request, "eligibleForExternalResearch") is not false)
                     .Take(8))
        {
            index++;
            var claim = ReadJsonString(request, "claim")
                ?? string.Empty;
            var readerActionability = ReadJsonString(request, "readerActionability") ?? "informational";
            var currentImpact = ReadJsonString(request, "currentImpact") ?? "context_needed";
            // A claim can require an update only when it can realistically guide
            // a present-day decision. This protects historical reporting and
            // informational trend analysis from being treated as live guidance.
            if (currentImpact == "update_required" && readerActionability != "decision_guiding")
            {
                currentImpact = "context_needed";
            }
            var node = new JsonObject
            {
                ["id"] = string.IsNullOrWhiteSpace(ReadJsonString(request, "id"))
                    ? $"claim-{index}"
                    : ReadJsonString(request, "id"),
                ["claim"] = ReadJsonString(request, "atomicClaim") ?? claim,
                ["pageQuote"] = ReadJsonString(request, "pageQuote") ?? string.Empty,
                ["atomicClaim"] = ReadJsonString(request, "atomicClaim") ?? claim,
                ["context"] = BuildContextArray(request),
                ["scope"] = CopyJsonStringArray(request, "scope"),
                ["whyMaterial"] = ReadJsonString(request, "whyMaterial") ?? string.Empty,
                ["presentedAsCurrent"] = ReadJsonBool(request, "presentedAsCurrent") ?? false,
                ["requiresExactScope"] = ReadJsonBool(request, "requiresExactScope") ?? false,
                ["claimType"] = ReadJsonString(request, "claimType") ?? "other",
                ["timeScope"] = ReadJsonString(request, "timeScope") ?? "timeless",
                ["volatility"] = ReadJsonString(request, "volatility") ?? "stable",
                ["risk"] = ReadJsonString(request, "risk") ?? "standard",
                ["claimNature"] = ReadJsonString(request, "claimNature") ?? "current_state",
                ["readerActionability"] = readerActionability,
                ["currentImpact"] = currentImpact,
                ["coverageGroup"] = ReadJsonString(request, "coverageGroup")
                    ?? ReadJsonString(request, "atomicClaim")
                    ?? claim,
                ["readerImpact"] = ReadJsonString(request, "readerImpact") ?? "medium",
                ["selectionReason"] = ReadJsonString(request, "selectionReason") ?? string.Empty,
            };
            using var document = JsonDocument.Parse(node.ToJsonString(JsonOptions));
            normalized.Add(document.RootElement.Clone());
        }

        return normalized;
    }

    private static List<JsonElement> KeepClaimsWithVisibleQuotes(
        IReadOnlyList<JsonElement> claims,
        string pageContent)
    {
        var normalizedContent = NormalizeWhitespace(pageContent);
        return claims.Where(claim =>
        {
            var quote = NormalizeWhitespace(ReadJsonString(claim, "pageQuote"));
            return quote.Length >= 12
                && normalizedContent.Contains(quote, StringComparison.OrdinalIgnoreCase);
        }).ToList();
    }

    private static List<JsonElement> SelectVerificationUnits(IReadOnlyList<JsonElement> claims)
    {
        var representatives = claims
            .Select((claim, index) => new
            {
                Claim = claim,
                Index = index,
                CoverageGroup = ReadJsonString(claim, "coverageGroup") ?? ReadJsonString(claim, "id") ?? $"claim-{index}",
                Priority = GetVerificationPriority(claim)
            })
            .GroupBy(item => item.CoverageGroup, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.Priority).ThenBy(item => item.Index).First())
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.Index)
            .ToList();

        var hasCurrentHighImpactUnit = representatives.Any(item => item.Priority >= 90);
        var budget = hasCurrentHighImpactUnit ? 5 : 2;
        return representatives.Take(budget).Select(item => item.Claim).ToList();
    }

    private static int GetVerificationPriority(JsonElement claim)
    {
        var presentedAsCurrent = ReadJsonBool(claim, "presentedAsCurrent") == true;
        var claimType = ReadJsonString(claim, "claimType");
        var risk = ReadJsonString(claim, "risk");
        var impact = ReadJsonString(claim, "readerImpact");

        var actionability = ReadJsonString(claim, "readerActionability");
        var currentImpact = ReadJsonString(claim, "currentImpact");

        if (currentImpact == "update_required" && actionability == "decision_guiding") return 105;
        if (presentedAsCurrent && claimType == "availability_status") return 100;
        if (presentedAsCurrent && risk == "high_stakes") return 95;
        if (presentedAsCurrent && impact == "high") return 90;
        if (currentImpact == "update_required") return 85;
        if (risk == "high_stakes") return 80;
        if (impact == "high") return 70;
        if (presentedAsCurrent) return 60;
        if (impact == "medium") return 50;
        return 40;
    }

    private static void NormalizeIssuesAndClassification(JsonObject node)
    {
        var issues = node["issues"] as JsonArray ?? new JsonArray();
        node["issues"] = DeduplicateIssueArray(issues);
        var hasIssues = ((JsonArray)node["issues"]!).Count > 0;

        node["classification"] = new JsonObject
        {
            ["type"] = hasIssues ? "unhealthy" : "healthy",
            ["confidence"] = ReadJsonDoubleFromNode(node, "classification", "confidence") ?? 0.95,
            ["insufficientEvidence"] = ReadJsonBoolFromNode(node, "classification", "insufficientEvidence") ?? false
        };

        if (!hasIssues)
        {
            node["issue"] = null;
        }
    }

    private static LlmOutput NormalizeTemporalGrounding(LlmOutput stage, DateOnly analysisDate)
    {
        if (stage.ParsedJson is null) return stage;

        var node = JsonNode.Parse(stage.ParsedJson.Value.GetRawText())!.AsObject();
        var temporal = node["temporalAssessment"] as JsonObject;
        if (temporal is not null
            && TryGetIsoDate(temporal, "referenceEndDate", out var referenceEndDate)
            && referenceEndDate < analysisDate
            && string.Equals(temporal["referenceStatus"]?.GetValue<string>(), "future", StringComparison.Ordinal))
        {
            // This is a data-consistency correction, not an inference: an explicit
            // end date before analysisDate cannot describe a future reference.
            temporal["referenceStatus"] = "past";
        }

        using var document = JsonDocument.Parse(node.ToJsonString(JsonOptions));
        var parsed = document.RootElement.Clone();
        return new LlmOutput
        {
            Provider = stage.Provider,
            Model = stage.Model,
            RawResponse = stage.RawResponse,
            ParsedResponse = node.ToJsonString(JsonOptions),
            ParsedJson = parsed,
            Error = stage.Error,
            WebSourceUrls = stage.WebSourceUrls,
            RetrievedSourceContents = stage.RetrievedSourceContents
        };
    }

    private static LlmOutput ApplyDiagnosisContracts(
        LlmOutput stage,
        JsonElement intent,
        JsonElement fixedTemporalGrounding,
        int claimLedgerCount)
    {
        if (stage.ParsedJson is null) return stage;

        var node = JsonNode.Parse(stage.ParsedJson.Value.GetRawText())!.AsObject();
        var fixedTemporal = JsonNode.Parse(fixedTemporalGrounding
            .GetProperty("temporalAssessment").GetRawText())!.AsObject();

        // Diagnosis receives this assessment as fixed input. Keeping it verbatim
        // prevents a later stage from reinterpreting an already-grounded date.
        node["temporalAssessment"] = fixedTemporal;

        var intentType = ReadJsonString(intent, "intent", "type");
        var issues = node["issues"] as JsonArray ?? new JsonArray();
        if (string.Equals(intentType, "article", StringComparison.Ordinal))
        {
            RemoveIssueCode(issues, "missing_expected_next_step");

            var hasMaterialClaims = claimLedgerCount > 0;
            if (hasMaterialClaims)
            {
                // "Too little usable content" and one or more material claims
                // extracted from that content cannot both be true.
                RemoveIssueCode(issues, "insufficient_information");
            }
        }
        node["issues"] = issues;

        NormalizeIssuesAndClassification(node);
        using var document = JsonDocument.Parse(node.ToJsonString(JsonOptions));
        var parsed = document.RootElement.Clone();
        return new LlmOutput
        {
            Provider = stage.Provider,
            Model = stage.Model,
            RawResponse = stage.RawResponse,
            ParsedResponse = node.ToJsonString(JsonOptions),
            ParsedJson = parsed,
            Error = stage.Error
        };
    }

    private static bool TryGetIsoDate(JsonObject source, string propertyName, out DateOnly value)
    {
        value = default;
        return source[propertyName] is JsonValue property
            && property.TryGetValue<string>(out var text)
            && DateOnly.TryParseExact(text, "yyyy-MM-dd", out value);
    }

    private static void RemoveIssueCode(JsonArray issues, string issueCode)
    {
        for (var index = issues.Count - 1; index >= 0; index--)
        {
            if (issues[index] is JsonValue value
                && value.TryGetValue<string>(out var text)
                && string.Equals(text, issueCode, StringComparison.Ordinal))
            {
                issues.RemoveAt(index);
            }
        }
    }

    private static void EnsureIssueCode(JsonObject node, string issueCode)
    {
        var issues = node["issues"] as JsonArray ?? new JsonArray();
        if (!ContainsJsonString(issues, issueCode))
        {
            issues.Add(issueCode);
        }

        node["issues"] = DeduplicateIssueArray(issues);
    }

    private static JsonArray DeduplicateIssueArray(JsonArray issues)
    {
        var unique = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in issues)
        {
            var text = value?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(text) && seen.Add(text))
            {
                unique.Add(text);
            }
        }

        return unique;
    }

    private static bool ContainsJsonString(JsonArray array, string value) =>
        array.OfType<JsonValue>()
            .Select(item => item.TryGetValue<string>(out var text) ? text : null)
            .Any(text => string.Equals(text, value, StringComparison.Ordinal));

    private static List<string> ReadIssues(JsonElement element)
    {
        if (!element.TryGetProperty("issues", out var issues)
            || issues.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return issues.EnumerateArray()
            .Select(item => item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object => ReadJsonString(item, "code"),
                _ => null
            })
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static double? ReadJsonDoubleFromNode(JsonObject node, params string[] path)
    {
        using var document = JsonDocument.Parse(node.ToJsonString(JsonOptions));
        return ReadJsonDouble(document.RootElement, path);
    }

    private static bool? ReadJsonBoolFromNode(JsonObject node, params string[] path)
    {
        using var document = JsonDocument.Parse(node.ToJsonString(JsonOptions));
        return ReadJsonBool(document.RootElement, path);
    }

    private static LlmOutput ValidateFactCheckBatch(
        LlmOutput stage,
        IReadOnlyList<JsonElement> requestedClaims)
    {
        if (stage.ParsedJson is null)
        {
            return stage;
        }

        var nativeUrls = stage.WebSourceUrls
            .Select(NormalizeSourceUrl)
            .Where(url => url is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var returnedChecks = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (stage.ParsedJson.Value.TryGetProperty("checks", out var checks)
            && checks.ValueKind == JsonValueKind.Array)
        {
            foreach (var check in checks.EnumerateArray())
            {
                var claimId = ReadJsonString(check, "claimId");
                if (!string.IsNullOrWhiteSpace(claimId))
                {
                    returnedChecks[claimId] = check.Clone();
                }
            }
        }

        var validatedChecks = new JsonArray();
        foreach (var requestedClaim in requestedClaims)
        {
            var claimId = ReadJsonString(requestedClaim, "id") ?? string.Empty;
            var pageClaim = ReadJsonString(requestedClaim, "claim") ?? string.Empty;
            if (!returnedChecks.TryGetValue(claimId, out var returnedCheck))
            {
                validatedChecks.Add(CreateUnverifiableCheck(
                    claimId,
                    pageClaim,
                    "The fact-check response did not return a result for this claim."));
                continue;
            }

            var validatedSources = new JsonArray();
            var hasOfficialOrPrimarySource = false;
            if (returnedCheck.TryGetProperty("sources", out var sources)
                && sources.ValueKind == JsonValueKind.Array)
            {
                foreach (var source in sources.EnumerateArray())
                {
                    var sourceUrl = NormalizeSourceUrl(ReadJsonString(source, "url"));
                    if (sourceUrl is null || !nativeUrls.Contains(sourceUrl))
                    {
                        continue;
                    }

                    var sourceType = ReadJsonString(source, "sourceType") ?? "other";
                    hasOfficialOrPrimarySource |= sourceType is "official" or "primary";

                    validatedSources.Add(new JsonObject
                    {
                        ["title"] = ReadJsonString(source, "title") ?? string.Empty,
                        ["url"] = sourceUrl,
                        ["publisher"] = ReadJsonString(source, "publisher") ?? string.Empty,
                        ["sourceType"] = sourceType,
                        ["citationReturnedByTool"] = true
                    });
                }
            }

            var verdict = ResolveValidatedFactCheckVerdict(requestedClaim, returnedCheck);
            var evidenceValidation = ValidateEvidence(returnedCheck, nativeUrls, stage.RetrievedSourceContents);
            var validatedEvidence = evidenceValidation.Evidence;
            if (verdict is not "unverifiable" && validatedEvidence.Count == 0)
            {
                validatedChecks.Add(CreateUnverifiableCheck(
                    claimId,
                    ReadJsonString(returnedCheck, "pageClaim") ?? pageClaim,
                    "The proposed verdict was not accepted because none of its evidence quotes could be verified against a consulted source.",
                    evidenceValidation.Status));
                continue;
            }
            if (verdict is not "unverifiable" && validatedSources.Count == 0)
            {
                validatedChecks.Add(CreateUnverifiableCheck(
                    claimId,
                    ReadJsonString(returnedCheck, "pageClaim") ?? pageClaim,
                    "The proposed verdict was not accepted because none of its sources could be validated against the web-search tool output."));
                continue;
            }

            if (verdict is ("contradicted" or "outdated" or "partially_supported")
                && !hasOfficialOrPrimarySource)
            {
                validatedChecks.Add(CreateUnverifiableCheck(
                    claimId,
                    ReadJsonString(returnedCheck, "pageClaim") ?? pageClaim,
                    "The proposed verdict was not accepted because no official or primary source was returned by the web-search tool."));
                continue;
            }

            if (!HasConsistentFactCheckOutcome(requestedClaim, returnedCheck, verdict))
            {
                validatedChecks.Add(CreateUnverifiableCheck(
                    claimId,
                    ReadJsonString(returnedCheck, "pageClaim") ?? pageClaim,
                    "The proposed verdict was not accepted because its verdict, evidence relation, and reader-facing fact verdict were inconsistent."));
                continue;
            }

            validatedChecks.Add(new JsonObject
            {
                ["claimId"] = claimId,
                ["verdict"] = verdict,
                ["evidenceRelation"] = MapEvidenceRelation(verdict),
                ["factVerdict"] = MapFactVerdict(verdict),
                ["confidence"] = ReadJsonDouble(returnedCheck, "confidence") ?? 0,
                ["pageClaim"] = ReadJsonString(returnedCheck, "pageClaim") ?? pageClaim,
                ["currentFact"] = ReadJsonString(returnedCheck, "currentFact"),
                ["reason"] = ReadJsonString(returnedCheck, "reason") ?? string.Empty,
                ["evidenceStatus"] = "validated",
                ["historicalValidity"] = NormalizeHistoricalValidity(ReadJsonString(returnedCheck, "historicalValidity"), verdict),
                ["currentValidity"] = NormalizeCurrentValidity(ReadJsonString(returnedCheck, "currentValidity"), verdict),
                ["evidenceScope"] = CopyJsonStringArray(returnedCheck, "evidenceScope"),
                ["evidenceQuote"] = ReadJsonString(returnedCheck, "evidenceQuote") ?? string.Empty,
                ["evidence"] = validatedEvidence,
                ["sources"] = validatedSources
            });
        }

        var result = new JsonObject
        {
            ["summary"] = ReadJsonString(stage.ParsedJson.Value, "summary") ?? string.Empty,
            ["checks"] = validatedChecks
        };
        using var resultDocument = JsonDocument.Parse(result.ToJsonString(JsonOptions));
        var parsed = resultDocument.RootElement.Clone();
        return new LlmOutput
        {
            Provider = stage.Provider,
            Model = stage.Model,
            RawResponse = stage.RawResponse,
            ParsedResponse = result.ToJsonString(JsonOptions),
            ParsedJson = parsed,
            Error = stage.Error,
            WebSourceUrls = stage.WebSourceUrls,
            RetrievedSourceContents = stage.RetrievedSourceContents
        };
    }

    private static JsonObject CreateUnverifiableCheck(
        string claimId,
        string pageClaim,
        string reason,
        string evidenceStatus = "inconclusive") => new()
    {
        ["claimId"] = claimId,
        ["verdict"] = "unverifiable",
        ["evidenceRelation"] = "insufficient_evidence",
        ["factVerdict"] = "insufficient_evidence",
        ["confidence"] = 0,
        ["pageClaim"] = pageClaim,
        ["currentFact"] = null,
        ["reason"] = reason,
        ["evidenceStatus"] = evidenceStatus,
        ["historicalValidity"] = "unknown",
        ["currentValidity"] = "unknown",
        ["evidenceScope"] = new JsonArray(),
        ["evidenceQuote"] = string.Empty,
        ["evidence"] = new JsonArray(),
        ["sources"] = new JsonArray()
    };

    private static string MapEvidenceRelation(string verdict) => verdict switch
    {
        "supported" => "supports",
        "contradicted" or "outdated" => "contradicts",
        "partially_supported" => "partially_supports",
        _ => "insufficient_evidence"
    };

    private static string MapFactVerdict(string verdict) => verdict switch
    {
        "supported" => "verified",
        "contradicted" => "false",
        "outdated" => "outdated",
        "partially_supported" => "partially_verified",
        _ => "insufficient_evidence"
    };

    private static bool HasConsistentFactCheckOutcome(
        JsonElement requestedClaim,
        JsonElement returnedCheck,
        string validatedVerdict)
    {
        var returnedEvidenceRelation = ReadJsonString(returnedCheck, "evidenceRelation");
        var returnedFactVerdict = ReadJsonString(returnedCheck, "factVerdict");

        if (validatedVerdict == "supported"
            && (ReadJsonBool(requestedClaim, "presentedAsCurrent") == true
                || ReadJsonString(requestedClaim, "currentImpact") == "update_required"))
        {
            var historicalValidity = NormalizeHistoricalValidity(
                ReadJsonString(returnedCheck, "historicalValidity"),
                validatedVerdict);
            var currentValidity = NormalizeCurrentValidity(
                ReadJsonString(returnedCheck, "currentValidity"),
                validatedVerdict);

            if (historicalValidity == "contradicted" || currentValidity is "outdated" or "contradicted")
            {
                return false;
            }
        }

        return string.Equals(returnedEvidenceRelation, MapEvidenceRelation(validatedVerdict), StringComparison.Ordinal)
            && string.Equals(returnedFactVerdict, MapFactVerdict(validatedVerdict), StringComparison.Ordinal);
    }

    private static JsonArray CopyJsonStringArray(JsonElement source, string propertyName)
    {
        var result = new JsonArray();
        foreach (var value in ReadJsonArrayValues(source, propertyName)) result.Add(value);
        return result;
    }

    private static bool IsEvidenceQuotePresentInSources(
        string? evidenceQuote,
        JsonArray sources,
        IReadOnlyDictionary<string, string>? retrievedSourceContents = null)
    {
        var quote = NormalizeWhitespace(evidenceQuote);
        if (quote.Length < 20) return false;
        foreach (var source in sources.OfType<JsonObject>())
        {
            var url = source["url"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(url)) continue;
            var content = GetValidationSourceText(url, retrievedSourceContents);
            if (content is not null && IsExactQuoteInSource(quote, content)) return true;
        }
        return false;
    }

    private static EvidenceValidationResult ValidateEvidence(
        JsonElement returnedCheck,
        ISet<string> nativeUrls,
        IReadOnlyDictionary<string, string>? retrievedSourceContents)
    {
        var result = new JsonArray();
        if (!returnedCheck.TryGetProperty("evidence", out var evidence)
            || evidence.ValueKind != JsonValueKind.Array)
        {
            return new EvidenceValidationResult(result, "no_evidence_returned");
        }

        var hadCandidateEvidence = false;
        var encounteredPdfSource = false;

        foreach (var item in evidence.EnumerateArray())
        {
            var sourceUrl = NormalizeSourceUrl(ReadJsonString(item, "sourceUrl"));
            var quote = NormalizeWhitespace(ReadJsonString(item, "quote"));
            if (sourceUrl is null || !nativeUrls.Contains(sourceUrl) || quote.Length < 20)
            {
                continue;
            }

            hadCandidateEvidence = true;
            if (IsPdfSourceUrl(sourceUrl))
            {
                encounteredPdfSource = true;
                continue;
            }

            var content = GetValidationSourceText(sourceUrl, retrievedSourceContents);
            if (content is null || !IsExactQuoteInSource(quote, content))
            {
                continue;
            }

            result.Add(new JsonObject
            {
                ["sourceUrl"] = sourceUrl,
                ["sourceType"] = ReadJsonString(item, "sourceType") ?? "other",
                ["quote"] = quote,
                ["scope"] = CopyJsonStringArray(item, "scope")
            });
        }

        return result.Count > 0
            ? new EvidenceValidationResult(result, "validated")
            : new EvidenceValidationResult(
                result,
                encounteredPdfSource ? "unsupported_pdf_source" : hadCandidateEvidence ? "citation_validation_failed" : "no_evidence_returned");
    }

    private static bool IsPdfSourceUrl(string sourceUrl) =>
        Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri)
        && uri.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    private static bool IsExactQuoteInSource(string quote, string sourceContent)
    {
        if (sourceContent.Contains(quote, StringComparison.OrdinalIgnoreCase)) return true;

        // An ellipsis is a formal omission marker, not a request for fuzzy matching.
        // Validate each literal segment in order, with no skipped segment accepted.
        var segments = quote
            .Split(["...", "…"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeWhitespace)
            .ToArray();
        if (segments.Length < 2 || segments.Any(segment => segment.Length < 12)) return false;

        var offset = 0;
        foreach (var segment in segments)
        {
            var index = sourceContent.IndexOf(segment, offset, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            offset = index + segment.Length;
        }

        return true;
    }

    private static string? GetValidationSourceText(
        string sourceUrl,
        IReadOnlyDictionary<string, string>? retrievedSourceContents)
    {
        if (retrievedSourceContents is not null)
        {
            return retrievedSourceContents.TryGetValue(sourceUrl, out var retrievedContent)
                ? retrievedContent
                : null;
        }

        return TryGetSourceText(sourceUrl);
    }

    private static string? TryGetSourceText(string sourceUrl)
    {
        try
        {
            using var response = SourceValidationHttpClient.GetAsync(sourceUrl).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (mediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                var document = BrowsingContext.New(Configuration.Default)
                    .OpenAsync(request => request.Content(content))
                    .GetAwaiter()
                    .GetResult();
                content = document.Body?.TextContent ?? document.DocumentElement?.TextContent ?? content;
            }

            return NormalizeWhitespace(content);
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
    }

    private static LlmOutput MergeFactCheckBatches(IReadOnlyList<LlmOutput> batches)
    {
        var checks = new JsonArray();
        var summaries = new List<string>();
        foreach (var batch in batches)
        {
            if (batch.ParsedJson is null)
            {
                continue;
            }

            var summary = ReadJsonString(batch.ParsedJson.Value, "summary");
            if (!string.IsNullOrWhiteSpace(summary))
            {
                summaries.Add(summary);
            }

            if (batch.ParsedJson.Value.TryGetProperty("checks", out var batchChecks)
                && batchChecks.ValueKind == JsonValueKind.Array)
            {
                foreach (var check in batchChecks.EnumerateArray())
                {
                    checks.Add(JsonNode.Parse(check.GetRawText()));
                }
            }
        }

        var merged = new JsonObject
        {
            ["summary"] = string.Join(" ", summaries),
            ["checks"] = checks
        };
        using var document = JsonDocument.Parse(merged.ToJsonString(JsonOptions));
        var parsed = document.RootElement.Clone();
        return new LlmOutput
        {
            Provider = batches.FirstOrDefault()?.Provider ?? AppConstants.Provider,
            Model = batches.FirstOrDefault()?.Model ?? GetStageModel(LlmStage.FactCheck),
            RawResponse = JsonSerializer.Serialize(batches.Select(batch => batch.RawResponse).ToArray(), JsonOptions),
            ParsedResponse = merged.ToJsonString(JsonOptions),
            ParsedJson = parsed,
            WebSourceUrls = batches.SelectMany(batch => batch.WebSourceUrls).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private static bool NeedsAuthoritativeSourceRetry(
        LlmOutput unvalidatedStage,
        IReadOnlyList<JsonElement> requestedClaims)
    {
        if (unvalidatedStage.ParsedJson is null
            || !unvalidatedStage.ParsedJson.Value.TryGetProperty("checks", out var checks)
            || checks.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var highPriorityIds = requestedClaims
            .Select(request => ReadJsonString(request, "id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        foreach (var check in checks.EnumerateArray())
        {
            var claimId = ReadJsonString(check, "claimId");
            var verdict = ReadJsonString(check, "verdict");
            if (claimId is null
                || !highPriorityIds.Contains(claimId)
                || verdict is not ("contradicted" or "outdated" or "partially_supported"))
            {
                continue;
            }

            var hasAuthoritativeSource = check.TryGetProperty("sources", out var sources)
                && sources.ValueKind == JsonValueKind.Array
                && sources.EnumerateArray().Any(source =>
                    ReadJsonString(source, "sourceType") is "official" or "primary");
            if (!hasAuthoritativeSource)
            {
                return true;
            }
        }

        return false;
    }

    private static LlmOutput CombineFactCheckAttempts(LlmOutput first, LlmOutput retry) => new()
    {
        Provider = retry.Provider,
        Model = retry.Model,
        RawResponse = JsonSerializer.Serialize(new
        {
            initialAttempt = first.RawResponse,
            authoritativeRetry = retry.RawResponse
        }, JsonOptions),
        ParsedResponse = retry.ParsedResponse,
        ParsedJson = retry.ParsedJson,
        Error = retry.Error,
        WebSourceUrls = first.WebSourceUrls
            .Concat(retry.WebSourceUrls)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
    };

    private static async Task<LlmOutput> EvaluateEvidenceRelationsAsync(
        LlmOutput factCheckStage,
        IReadOnlyList<JsonElement> requestedClaims)
    {
        if (factCheckStage.ParsedJson is null
            || !factCheckStage.ParsedJson.Value.TryGetProperty("checks", out var checks)
            || checks.ValueKind != JsonValueKind.Array)
        {
            return factCheckStage;
        }

        var evaluationStage = await ExecuteLlmStageAsync(
            LlmStage.EvidenceEvaluation,
            EvidenceEvaluationSystemPrompt,
            BuildEvidenceEvaluationPrompt(checks, requestedClaims),
            EvidenceEvaluationResponseSchemaName,
            EvidenceEvaluationResponseSchemaJson);
        if (!string.IsNullOrWhiteSpace(evaluationStage.Error) || evaluationStage.ParsedJson is null
            || !evaluationStage.ParsedJson.Value.TryGetProperty("checks", out var evaluations)
            || evaluations.ValueKind != JsonValueKind.Array)
        {
            return factCheckStage;
        }

        var relationsByClaimId = evaluations.EnumerateArray()
            .Select(item => new { ClaimId = ReadJsonString(item, "claimId"), Relation = ReadJsonString(item, "relation") })
            .Where(item => !string.IsNullOrWhiteSpace(item.ClaimId) && !string.IsNullOrWhiteSpace(item.Relation))
            .ToDictionary(item => item.ClaimId!, item => item.Relation!, StringComparer.Ordinal);

        var evaluatedChecks = new JsonArray();
        foreach (var check in checks.EnumerateArray())
        {
            var claimId = ReadJsonString(check, "claimId") ?? string.Empty;
            var request = requestedClaims.FirstOrDefault(candidate =>
                string.Equals(ReadJsonString(candidate, "id"), claimId, StringComparison.Ordinal));
            if (!relationsByClaimId.TryGetValue(claimId, out var relation))
            {
                evaluatedChecks.Add(CreateUnverifiableCheck(
                    claimId,
                    ReadJsonString(check, "pageClaim") ?? string.Empty,
                    "The evidence-relation evaluator did not return a result for this claim."));
                continue;
            }

            var originalVerdict = ReadJsonString(check, "verdict") ?? "unverifiable";
            if (relation == "insufficient_evidence" && originalVerdict != "unverifiable")
            {
                evaluatedChecks.Add(JsonNode.Parse(check.GetRawText()));
                continue;
            }
            var evaluatedVerdict = relation switch
            {
                "supports" => "supported",
                "contradicts" when originalVerdict == "outdated" => "outdated",
                "contradicts" => "contradicted",
                "partially_supports" => "partially_supported",
                _ => "unverifiable"
            };
            if (request.ValueKind == JsonValueKind.Object
                && ReadJsonString(request, "currentImpact") == "update_required"
                && ReadJsonString(check, "historicalValidity") == "supported")
            {
                var currentValidity = ReadJsonString(check, "currentValidity");
                if (currentValidity == "outdated" && evaluatedVerdict == "supported")
                {
                    evaluatedVerdict = "outdated";
                }
                else if (currentValidity == "contradicted" && evaluatedVerdict == "supported")
                {
                    evaluatedVerdict = "contradicted";
                }
            }
            if (evaluatedVerdict == "unverifiable")
            {
                evaluatedChecks.Add(CreateUnverifiableCheck(
                    claimId,
                    ReadJsonString(check, "pageClaim") ?? string.Empty,
                    "The accepted evidence was insufficient to establish a relation to the claim."));
                continue;
            }

            var evaluatedCheck = JsonNode.Parse(check.GetRawText())!.AsObject();
            evaluatedCheck["verdict"] = evaluatedVerdict;
            evaluatedCheck["evidenceRelation"] = MapEvidenceRelation(evaluatedVerdict);
            evaluatedCheck["factVerdict"] = MapFactVerdict(evaluatedVerdict);
            if (evaluatedVerdict == "contradicted")
            {
                evaluatedCheck["currentValidity"] = "contradicted";
            }
            else if (evaluatedVerdict == "outdated")
            {
                evaluatedCheck["currentValidity"] = "outdated";
            }
            evaluatedChecks.Add(evaluatedCheck);
        }

        var merged = new JsonObject
        {
            ["summary"] = ReadJsonString(factCheckStage.ParsedJson.Value, "summary") ?? string.Empty,
            ["checks"] = evaluatedChecks
        };
        using var document = JsonDocument.Parse(merged.ToJsonString(JsonOptions));
        return new LlmOutput
        {
            Provider = factCheckStage.Provider,
            Model = factCheckStage.Model,
            RawResponse = JsonSerializer.Serialize(new { factCheck = factCheckStage.RawResponse, evidenceEvaluation = evaluationStage.RawResponse }, JsonOptions),
            ParsedResponse = merged.ToJsonString(JsonOptions),
            ParsedJson = document.RootElement.Clone(),
            Error = factCheckStage.Error,
            WebSourceUrls = factCheckStage.WebSourceUrls,
            RetrievedSourceContents = factCheckStage.RetrievedSourceContents
        };
    }

    private const string IntentSystemPrompt = """
        You are a senior page-intent classifier.
        Your task is to identify the primary purpose a web page serves for visitors.
        Classify the page intent only. Do not evaluate content quality, freshness, SEO, accuracy, or whether the page should be improved.

        You will receive:
        - url: the page URL
        - canonicalUrl: the page's declared canonical URL, when available
        - pageTitle: the page title
        - contentDate: the best available editorial date for the page, if available
        - pageWordCount: approximate word count of the cleaned page content
        - metadataDescription: the page meta description
        - metadataAuthor: the page meta author, if available
        - pageContent: the cleaned page content in markdown format
        - outputLanguage: the language to use for all explanatory output fields

        Use the page content as the primary source of truth.
        Use the URL, title, metadata description, author, and contentDate only as supporting context.
        Use pageWordCount as a supporting signal for content thickness. Very short pages with a title, teaser, and CTA but no substantial body often fit referral_stub better than article.
        Do not infer intent from the URL alone if the page content suggests a different dominant purpose.

        Choose exactly one intent from this list:
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

        Decision rules:
        - Always classify by the dominant user purpose, not by isolated elements.
        - Do not choose contact only because the page contains an email, phone number, address, map, or generic contact form.
        - Do not choose pricing only because the page briefly mentions cost, price, or payment.
        - Choose pricing when plans, tiers, included features, billing options, or package comparisons are the main content, even when numeric prices are replaced by "contact sales", "request a quote", or equivalent sales-led pricing.
        - Do not choose faq unless the main structure of the page is question-and-answer.
        - Choose article when the page's main value is direct explanation, guidance, commentary, or educational content.
        - Choose hub_index when the page mainly helps users navigate to multiple other pages or items.
        - Choose referral_stub only when the page is short and clearly exists mainly to send users to one primary destination, featured asset, or next step instead of delivering substantial standalone content on the page itself.
        - Choose landing when the page is promotional and broad, especially when it introduces a company, campaign, audience, practice area, or general service category.
        - Choose product when the page focuses in depth on one specific service, product, offer, solution, or feature.
        - Choose announcement when the page is mainly a dated update or news-style notice from an organization.
        - Choose event_coverage only when the page reports on or recaps a specific named event.
        - Choose composite_page only when two or more intents are equally strong and no dominant purpose can be determined.
        - Choose unknown only when the readable content is unusable, incoherent, or insufficient to identify a purpose.

        Tie-breaking guidance:
        - If a page lists many articles, services, locations, resources, or links, prefer hub_index over landing.
        - If a page looks like an article shell but only provides a teaser and a featured link or CTA to the real content, prefer referral_stub over article.
        - If a page promotes a broad service area but does not deeply describe one concrete offer, prefer landing over product.
        - If a page explains a topic in depth and also promotes the organization, prefer article when the explanation is the main value.
        - If a page announces something but also explains background information, prefer announcement when the update itself is the main reason the page exists.
        - If a page contains multiple sections such as overview, services, FAQs, contact, and calls to action, choose the section that best represents the dominant user task.

        Output requirements:
        - Return only valid JSON matching the provided schema.
        - Do not include markdown.
        - Do not include extra properties.
        - Write pageSummary and rationale in the outputLanguage language.
        - The pageSummary must be concise and describe what the page is about, not whether it is good or bad.
        - The rationale must explain why the selected intent is the dominant one and be grounded in visible page evidence.
        """;

    private const string IntentResponseSchemaName = "page_intent";

    private const string IntentResponseSchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["intent", "pageSummary"],
          "properties": {
            "intent": {
              "type": "object",
              "additionalProperties": false,
              "required": ["type", "confidence", "rationale"],
              "properties": {
                "type": {
                  "type": "string",
                  "enum": [
                    "article",
                    "referral_stub",
                    "hub_index",
                    "pricing",
                    "product",
                    "contact",
                    "landing",
                    "faq",
                    "legal",
                    "support_doc",
                    "profile_bio",
                    "composite_page",
                    "announcement",
                    "event_coverage",
                    "job_posting",
                    "unknown"
                  ]
                },
                "confidence": {
                  "type": "number",
                  "minimum": 0,
                  "maximum": 1
                },
                "rationale": { "type": "string" }
              }
            },
            "pageSummary": {
              "type": "string",
              "minLength": 20,
              "maxLength": 700
            }
          }
        }
        """;

    private const string TemporalGroundingSystemPrompt = """
        You are a temporal grounding extractor.

        Your task is only to determine how the page is positioned in time relative to analysisDate.

        Do not diagnose editorial quality.
        Do not suggest improvements.
        Do not select verification claims.
        Do not use external knowledge.

        You will receive:

        * analysisDate: the date of evaluation
        * outputLanguage: the language for human-facing output text
        * url: the page URL
        * pageTitle: the page title
        * metadataDescription: the page meta description
        * pageLanguage: the page language, if available
        * contentDate: the best available editorial date for the page, if available
        * fixedIntent: the already-decided page intent
        * fixedIntentRationale: why that intent was chosen
        * pageContent: the cleaned page content in markdown format

        Rules:

        * Work only from visible page evidence.
        * First identify the page's main referenced moment, event, cycle, deadline, status period, or time target.
        * Write that in primaryReferencedMomentText.
        * If the page gives an explicit date, year, period, edition, or named event cycle, write it in primaryReferencedMomentDateOrPeriod.
        * If that moment has a definite end date or deadline visible on the page, also return it as referenceEndDate in ISO format (YYYY-MM-DD). Use null when the page does not establish one. Do not guess a date.
        * Then compare that extracted moment against analysisDate.
        * referenceStatus must be strictly relative to analysisDate, not relative to the page's publication date or writing tone.
        * Never infer referenceStatus only from words like upcoming, register, look forward to, will, or don't miss.
        * If the page title or body explicitly names a past year, date, edition, event cycle, or deadline before analysisDate, referenceStatus cannot be `future` unless the page clearly points to a later replacement moment.
        * orientation describes how the page positions itself in time:
          - `preview`: mainly anticipates something expected later.
          - `historical_record`: mainly records something that already happened.
          - `current_state`: mainly describes a current rule, status, availability, or support condition.
          - `evergreen`: mainly teaches or explains something not tied to a specific moment.
          - `mixed`: more than one orientation is materially present.
          - `unclear`: time posture cannot be determined confidently.
        * referenceStatus must be one of: `future`, `present`, `past`, `mixed`, `unclear`.
        * appearsCurrentToReader is true when a reasonable reader could treat the page as current guidance, availability, or invitation to act now.
        * Legal, immigration, eligibility, tax, financial, medical, safety, or other procedural guidance can appear current without an explicit marketing CTA. When such a page tells readers who can apply, what they must submit, where to file, or the deadline they must meet, treat that operational guidance as current unless the page clearly labels it as archived or historical only.
        * A visible operational CTA has precedence over the page's historical discussion when deciding appearsCurrentToReader. Treat an imperative invitation to take part in the referenced event or cycle — for example register today, don't miss your chance, join us, attend, book now, apply now, or an equivalent — as a live invitation, not merely historical wording.
        * Therefore, when an explicitly past event or cycle is still paired with such a CTA, set orientation=`preview`, referenceStatus=`past`, and appearsCurrentToReader=true. Do not set appearsCurrentToReader=false merely because the article also discusses the event in historical terms or has an old contentDate.
        * historicalContextClear is true when the page clearly frames itself as archive, recap, announcement record, or completed event record.
        * Keep the output internally consistent.

        Output requirements:
        * Return only valid JSON matching the provided schema.
        * Do not include markdown.
        * Do not include extra properties.
        * Write human-facing text in outputLanguage.
        * Keep JSON field names and enum values in English.
        """;

    private const string TemporalGroundingResponseSchemaName = "page_temporal_grounding";

    private const string TemporalGroundingResponseSchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["temporalAssessment"],
          "properties": {
            "temporalAssessment": {
              "type": "object",
              "additionalProperties": false,
              "required": ["primaryReferencedMomentText", "primaryReferencedMomentDateOrPeriod", "referenceEndDate", "orientation", "referenceStatus", "appearsCurrentToReader", "historicalContextClear", "rationale"],
              "properties": {
                "primaryReferencedMomentText": { "type": "string" },
                "primaryReferencedMomentDateOrPeriod": { "type": "string" },
                "referenceEndDate": { "type": ["string", "null"], "description": "Explicit page deadline or end date in YYYY-MM-DD format." },
                "orientation": {
                  "type": "string",
                  "enum": ["preview", "historical_record", "current_state", "evergreen", "mixed", "unclear"]
                },
                "referenceStatus": {
                  "type": "string",
                  "enum": ["future", "present", "past", "mixed", "unclear"]
                },
                "appearsCurrentToReader": { "type": "boolean" },
                "historicalContextClear": { "type": "boolean" },
                "rationale": { "type": "string" }
              }
            }
          }
        }
        """;

    private const string DiagnosisSystemPrompt = """
        You are a senior content health classifier.

        Your task is to decide whether a web page is editorially healthy for a fixed page intent and temporal grounding.
        Do not perform SEO analysis.
        Do not suggest improvements.        

        You will receive:

        * analysisDate: the date of evaluation
        * outputLanguage: the language for human-facing output text
        * url: the page URL
        * pageTitle: the page title
        * metadataDescription: the page meta description
        * pageLanguage: the page language, if available
        * contentDate: the best available editorial date for the page, if available
        * fixedIntent: the already-decided page intent
        * fixedTemporalGrounding: the precomputed temporal assessment to treat as authoritative unless pageContent directly contradicts it
        * pageContent: the cleaned page content in markdown format

        Allowed issues:

        * insufficient_information
        * unclear_messaging
        * within_page_inconsistency
        * title_body_mismatch
        * url_content_mismatch
        * missing_trust_context
        * missing_expected_next_step
        * brand_safety_risk
        * sensitive_topic_without_context
        * language_mismatch
        * expired_date_or_deadline
        * misleading_current_status

        Core decision rules:

        * Use pageContent as the primary source of truth.
        * Use metadata and contentDate only as supporting context.
        * Do not use remembered external knowledge to establish an internal issue, even when you are confident that a vendor, law, event, programme, product, or standard changed. External claim verification is handled by a separate stage.
        * Judge the page only against fixedIntent.
        * Focus on the main editorial content. Treat navigation, footer, sharing widgets, comments, contact blocks, and generic blog headings as boilerplate unless they materially affect the page's intent.
        * Default to healthy unless there is a concrete editorial defect with material user impact.
        * Before returning healthy, compare pageLanguage with the main reader-facing body. If they materially differ, include language_mismatch regardless of audience, brand positioning, or conversion quality.
        * Do not mark a page unhealthy only because it could be better.
        * For a pricing page, sales-led pricing can be complete without public numeric prices when the page clearly presents the available plans, their differences, and a contact-sales or quote-request next step. Do not classify that pattern as insufficient_information merely because prices are not public.
        * For a referral_stub, judge whether its destination and next step are clear; do not require the standalone depth expected from an article or product page.
        * Return every material internal issue in issues.
        * Use classification.type = healthy when issues is empty, otherwise unhealthy.
        * If a suspected issue is not directly supported by the visible content, do not flag it.
        * If evidence is limited or ambiguous, set classification.insufficientEvidence to true.

        Classification guidance:

        * healthy: the page is fit for its fixed intent and shows no clear editorial defect.
        * insufficient_information: there is too little usable content to assess whether the page fulfills its fixed intent.
        * unclear_messaging: the main message is confusing, vague, disorganized, or hard to follow.
        * within_page_inconsistency: the page contradicts itself in a material way.
        * title_body_mismatch: the title or major headings promise something the body does not deliver.
        * url_content_mismatch: the URL slug strongly suggests a page topic or promise that the main content does not actually deliver.
        * missing_trust_context: the page lacks important credibility context, such as source, author, date, method, attribution, or scope, when that context is necessary for the fixed intent.
        * missing_expected_next_step: the fixed intent requires the reader to take action, but the page does not make the expected next step clear. Use it only for transactional intents where an action is genuinely required.
        * brand_safety_risk: the page contains material that creates a meaningful reputational or policy risk.
        * sensitive_topic_without_context: the page discusses a sensitive topic without enough care, context, or framing.
        * language_mismatch: the page language does not match the expected audience or is internally mixed in a way that harms comprehension. This is mandatory when pageLanguage is present and a substantial reader-facing section is written in another language. Treat pageLanguage as the page's declared target language: an international or multilingual business is not an exception. Navigation labels, proper nouns, short quotations, and isolated legal terms do not count as substantial content; headings, explanatory paragraphs, service descriptions, market guidance, and calls to action do.
        * expired_date_or_deadline: the page explicitly presents an already-passed application, registration, promotion, offer, or action deadline as still actionable on analysisDate, and expiration is self-evident from the page's own date and CTA.
        * misleading_current_status: the page's own visible content presents a past or ended status as current, and that conclusion is directly supported without external research.

        Historical and temporal rules:

        * Historical recaps, archives, announcements, and event coverage that remain truthful in context must be classified as healthy.
        * A historical announcement does not become unhealthy merely because the event already happened, the page has no later update, or the topic is no longer current in the news cycle.
        * Do not require ongoing relevance, continued significance, or later developments for a truthful historical announcement to remain healthy.
        * Relative words such as today, now, this year, or next month are not defects when clearly anchored to a visible publication date and historical context.
        * Use fixedTemporalGrounding as temporalAssessment. Do not recompute it.
        * temporalAssessment.orientation describes how the page positions itself in time:
          - `preview`: the page mainly points readers toward something expected to happen or become available later.
          - `historical_record`: the page mainly records something that already happened.
          - `current_state`: the page mainly describes a status, rule, availability, support state, or condition that a reader would treat as currently true.
          - `evergreen`: the page mainly teaches or explains something not tied to a specific moment.
          - `mixed`: more than one orientation is materially present.
          - `unclear`: the time posture cannot be determined confidently.
        * temporalAssessment.referenceStatus compares the page's main referenced moment to analysisDate:
          - `future`, `present`, `past`, `mixed`, or `unclear`.
        * temporalAssessment.referenceStatus must be strictly relative to analysisDate, not relative to the page's publication date.
        * Keep temporalAssessment internally consistent. If the rationale says an event already happened before analysisDate, referenceStatus cannot be `future`. If the rationale says something is still upcoming after analysisDate, referenceStatus cannot be `past`.
        * temporalAssessment.appearsCurrentToReader is true when a reasonable reader could treat the page as current guidance, current availability, or current status.
        * Set appearsCurrentToReader=true when the page uses present-tense or action-oriented framing such as register, apply, join us, book now, act promptly, don't miss your chance, available now, remains active, currently supported, or equivalent current-action language.
        * A visible operational CTA takes precedence over historical discussion when deciding appearsCurrentToReader. If pageContent explicitly pairs a past event or cycle with a CTA such as register today, don't miss your chance, join us, attend, book now, or apply now, treat that CTA as a live invitation.
        * Planning language inside an article explicitly anchored to a past year, edition, or completed cycle is not itself a live invitation. A trends, forecast, or planning article about 2022 remains a historical record after 2022 unless the visible page separately invites the reader to register, apply, buy, attend, or take an equivalent present-day action for that past cycle.
        * In that case, if fixedTemporalGrounding says appearsCurrentToReader=false, pageContent directly contradicts it: change appearsCurrentToReader to true and preserve the past referenceStatus. This is a preview-style page that must receive expired_date_or_deadline; it cannot be healthy solely because the surrounding article is historical.
        * temporalAssessment.historicalContextClear is true when the page clearly frames itself as a historical recap, announcement archive, or completed event record.
        * An old date alone is never enough to make a page unhealthy.
        * Use expired_date_or_deadline only when the page itself supplies an expired action deadline or cycle and still presents that same action as available, or when a preview-style page is still inviting action for a moment that has already passed.
        * An old date, historical date, migration cutoff, version date, or technical date is not by itself an expired_date_or_deadline issue.
        * For a preview-style page:
          - if the referenced moment is still future, it can remain healthy;
          - if the referenced moment is already past and the page still reads like a live invitation, registration page, or upcoming-event preview, classify it as expired_date_or_deadline;
          - if the referenced moment is already past but the page no longer reads like a live invitation, it may remain healthy as a historical article or record.
        * When the page itself says that a route, programme, offer, process, version, event, or status is no longer available, ended, abolished, repealed, cancelled, discontinued, deprecated, replaced, or otherwise no longer current, but still uses present-tense commercial or action-oriented language that pushes the reader to proceed through that same route now, classify it as misleading_current_status.
        * In that situation, focus on the mismatch between the page's own status explanation and its live CTA or present-tense commercial framing. Do not require external research when the contradiction is already visible on the page itself.
        * Typical examples include pages that say a programme ended but still invite the reader to apply, book, start the process, proceed, or contact the company to obtain it now.
        * When deciding whether technical guidance, a product, a programme, an event, a rule, or a service remains valid requires information from its vendor, organizer, regulator, or another external authority, do not create an internal temporal issue.
        * For support documentation, a mentioned migration date, sunset announcement, legacy version, or vendor cutoff does not prove that the instructions are currently invalid. Verify the vendor's current status externally.
        * Use misleading_current_status only when the page itself contains enough evidence to establish the misleading status. The fact-check stage independently assesses externally governed claims.

        Special rules for url_content_mismatch:

        * Use it only when the URL slug is semantically specific enough to create a clear user expectation about the page topic.
        * Compare the URL promise against the main content, not against navigation, sidebars, or peripheral blocks.
        * Do not use it only because the slug is imperfect, legacy, shortened, branded, or slightly broader than the page.
        * Do not use it when the mismatch is better explained only by the title or headings; use title_body_mismatch in that case.

        Evidence rules:

        * For healthy pages, issue must be null and issues must be empty.
        * For unhealthy pages, issue must be present and issues must contain 1 or more entries.
        * issue.evidenceSnippets must contain 1 to 3 snippets that directly support the selected classification.
        * issue.whyFlagged must be fully grounded in the evidenceSnippets.
        * Do not infer broader risk, intent, implication, or reader confusion unless the cited snippets directly support it.
        * Classify based on the full visible context, not isolated fragments.
        * Use the minimum evidence needed to justify the issue.
        * Prefer snippets that are independently meaningful and tied to the narrowest visible section available.
        Output requirements:

        * Return only valid JSON matching the provided schema.
        * Do not include markdown.
        * Do not include extra properties.
        * Write all human-facing text in outputLanguage.
        * Keep JSON field names and enum values in English.                
        """;

    private const string DiagnosisResponseSchemaName = "page_diagnosis";

    private const string DiagnosisResponseSchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["classification", "issues", "classificationReasons", "temporalAssessment", "issue"],
          "properties": {
            "classification": {
              "type": "object",
              "additionalProperties": false,
              "required": ["type", "confidence", "insufficientEvidence"],
              "properties": {
                "type": {
                  "type": "string",
                  "enum": ["healthy", "unhealthy"]
                },
                "confidence": { "type": "number" },
                "insufficientEvidence": { "type": "boolean" }
              }
            },
            "issues": {
              "type": "array",
              "items": {
                "type": "string",
                "enum": [
                  "insufficient_information",
                  "unclear_messaging",
                  "within_page_inconsistency",
                  "title_body_mismatch",
                  "url_content_mismatch",
                  "missing_trust_context",
                  "missing_expected_next_step",
                  "brand_safety_risk",
                  "sensitive_topic_without_context",
                  "language_mismatch",
                  "expired_date_or_deadline",
                  "misleading_current_status",
                  "verified_current_claim_issue",
                  "verified_historical_claim_issue"
                ]
              }
            },
            "classificationReasons": {
              "type": "array",
              "minItems": 1,
              "maxItems": 4,
              "items": {
                "type": "string"
              }
            },
            "temporalAssessment": {
              "type": "object",
              "additionalProperties": false,
              "required": ["primaryReferencedMomentText", "primaryReferencedMomentDateOrPeriod", "referenceEndDate", "orientation", "referenceStatus", "appearsCurrentToReader", "historicalContextClear", "rationale"],
              "properties": {
                "primaryReferencedMomentText": { "type": "string" },
                "primaryReferencedMomentDateOrPeriod": { "type": "string" },
                "referenceEndDate": { "type": ["string", "null"], "description": "Explicit page deadline or end date in YYYY-MM-DD format." },
                "orientation": {
                  "type": "string",
                  "enum": ["preview", "historical_record", "current_state", "evergreen", "mixed", "unclear"]
                },
                "referenceStatus": {
                  "type": "string",
                  "enum": ["future", "present", "past", "mixed", "unclear"]
                },
                "appearsCurrentToReader": { "type": "boolean" },
                "historicalContextClear": { "type": "boolean" },
                "rationale": { "type": "string" }
              }
            },
            "issue": {
              "type": ["object", "null"],
              "additionalProperties": false,
              "required": ["title", "whyFlagged", "evidenceSnippets"],
              "properties": {
                "title": { "type": "string" },
                "whyFlagged": { "type": "string" },
                "evidenceSnippets": {
                  "type": "array",
                  "minItems": 1,
                  "maxItems": 3,
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["text", "context"],
                    "properties": {
                      "text": { "type": "string" },
                      "context": { "type": "string" }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private const string VerificationSelectionSystemPrompt = """
        You are a verification-request selector. Your only task is to decide which page claims should be researched against the public web.

        You receive the complete cleaned page content, fixed intent, and fixed temporal grounding. Extract claims directly from the page; do not rely on candidates from another stage.

        Return 0 to 8 evaluated claims. Set eligibleForExternalResearch=true only when all of these conditions hold:
        - it is a concrete factual assertion copied from the page;
        - being wrong would materially affect a reader;
        - a specific external authority owns the source of truth, such as a government, regulator, organizer, vendor, standards body, certifier, analyst firm, or original publisher;
        - public web research can reasonably confirm its status, date, validity, compatibility, certification, ranking, event status, rule, programme, or externally governed fact.

        Mark page-owner-only claims as eligibleForExternalResearch=false. Prices, discounts, trials, customer counts, internal uptime, savings, performance metrics, testimonials, private studies, product benefits, and the page owner's own policies are not externally verifiable merely because another website might mention them. The theoretical possibility of independent coverage is not a concrete external authority.

        Prefer operational claims that tell readers to continue, apply, register, migrate, buy, use, or rely on something whose current validity is controlled by an external vendor or authority. When a page contains both a historical announcement and current instructions based on it, select the current instruction as the main claim; the historical announcement may be included only if independently material. If a page says a programme, event, version, or route has ended or changed but still invites users to apply, register, contact the company to proceed, or rely on the old route now, select that live invitation or availability proposition and mark presentedAsCurrent=true.

        Distinguish authorship from governance. A page owner may write an instruction whose truth is controlled by someone else. For example, when a company tells readers to continue using a third-party vendor's SDK, API, integration, visa programme, certification, or regulated process, the vendor, organizer, certifier, or regulator is the external authority. Mark that instruction eligible even though the analyzed page authored it.

        Do not select subjective rhetoric, generic marketing comparisons, broad outcome promises, or claims whose precise entity cannot be resolved from the page URL, canonical URL, title, and content.

        pageQuote must be an exact contiguous quote from the visible page content and must contain the complete proposition being checked. atomicClaim must state exactly one independently verifiable proposition from pageQuote. Do not merge adjacent sentences, summarize a paragraph, or remove a material qualifier from pageQuote when forming atomicClaim. For a deadline, expiry, or application window presented as current guidance, retain the exact end date and the current operational availability; do not replace it with the underlying legal rule alone.
        claim must preserve every material entity, date, number, version, status, and attribution needed for verification. context must list only the short material filters required to verify correctly, especially for count, award, directory, ranking, or status claims. scope must list only explicitly relevant named filters, such as country, year, category, edition, role, jurisdiction, version, or population. presentedAsCurrent describes how the page uses the claim; historical claims remain eligible when materially verifiable. whyMaterial must briefly explain why the claim matters if wrong or outdated.

        Classify each eligible claim with these exact enums:
        - claimType: legal_regulatory, availability_status, product_support, award, event, corporate, numeric, other.
        - timeScope: historical, current, future, timeless.
        - volatility: stable, changing.
        - risk: standard, high_stakes. Use high_stakes for law, immigration, finance, health, safety, eligibility, or other decisions where an error can materially harm the reader.
        - claimNature: completed_event, time_bound_rule, current_state, revisable_knowledge, actionable_guidance. A completed event is fixed once it happened; revisable knowledge includes safety, efficacy, or causal claims that later evidence can supersede; actionable guidance tells a reader what they can do.
        - readerActionability: none, informational, decision_guiding. Use decision_guiding when a reader could reasonably act, apply, spend, use a treatment or product, or rely on the claim to make a material decision.
        - currentImpact: none, context_needed, update_required. This is independent from timeScope. Use none for a completed event whose later relevance cannot make the page misleading. Use context_needed when a historical claim benefits from a date or update note but cannot reasonably cause present harm. Use update_required only when readerActionability=decision_guiding and a current reader could still rely on the claim for a material decision; a historical claim can therefore be update_required. Informational reporting, retrospective trends, and completed-event reporting must use none or context_needed, never update_required.

        Set requiresExactScope=true for count, award, directory, ranking, listing, or similarly scoped claims. For those claims, context must contain every filter needed to prove or contradict the claim, such as year, country, category, edition, role, jurisdiction, version, and population. Set it false for ordinary claims.
        coverageGroup identifies the single external fact that controls this claim. Claims that would be resolved by the same research finding must use exactly the same concise group value, even when the page repeats them in a hero, FAQ, CTA, or body copy. Do not group claims with independently material consequences.
        readerImpact is high when a reader could act, spend money, apply, register, rely on legal/medical/financial guidance, or make another material decision; medium when the claim materially affects trust or understanding; low otherwise.
        selectionReason must explain why this is the representative claim to research for its coverageGroup. Prefer the operational current-status proposition over supporting details when both depend on the same external fact.
        Return only valid JSON matching the schema, without markdown or extra properties. Human-facing fields must use outputLanguage; JSON field names and enum values remain English.
        """;

    private const string VerificationSelectionResponseSchemaName = "verification_request_selection";

    private const string VerificationSelectionResponseSchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["verificationRequests"],
          "properties": {
            "verificationRequests": {
              "type": "array",
              "maxItems": 8,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": [
                  "id",
                  "claim",
                  "pageQuote",
                  "atomicClaim",
                  "context",
                  "scope",
                  "presentedAsCurrent",
                  "whyMaterial",
                  "requiresExactScope",
                  "claimType",
                  "timeScope",
                  "volatility",
                  "risk",
                  "claimNature",
                  "readerActionability",
                  "currentImpact",
                  "eligibleForExternalResearch",
                  "coverageGroup",
                  "readerImpact",
                  "selectionReason"
                ],
                "properties": {
                  "id": { "type": "string" },
                  "claim": { "type": "string" },
                  "pageQuote": { "type": "string", "minLength": 12 },
                  "atomicClaim": { "type": "string", "minLength": 8 },
                  "context": {
                    "type": "array",
                    "items": { "type": "string" }
                  },
                  "scope": { "type": "array", "items": { "type": "string" } },
                  "presentedAsCurrent": { "type": "boolean" },
                  "whyMaterial": { "type": "string" },
                  "requiresExactScope": { "type": "boolean" },
                  "claimType": { "type": "string", "enum": ["legal_regulatory", "availability_status", "product_support", "award", "event", "corporate", "numeric", "other"] },
                  "timeScope": { "type": "string", "enum": ["historical", "current", "future", "timeless"] },
                  "volatility": { "type": "string", "enum": ["stable", "changing"] },
                  "risk": { "type": "string", "enum": ["standard", "high_stakes"] },
                  "claimNature": { "type": "string", "enum": ["completed_event", "time_bound_rule", "current_state", "revisable_knowledge", "actionable_guidance"] },
                  "readerActionability": { "type": "string", "enum": ["none", "informational", "decision_guiding"] },
                  "currentImpact": { "type": "string", "enum": ["none", "context_needed", "update_required"] },
                  "eligibleForExternalResearch": { "type": "boolean" },
                  "coverageGroup": { "type": "string", "minLength": 3 },
                  "readerImpact": { "type": "string", "enum": ["high", "medium", "low"] },
                  "selectionReason": { "type": "string" }
                }
              }
            }
          }
        }
        """;

    private const string FactCheckSystemPrompt = """
        You are a senior fact checker.

        Your task is to research a supplied batch of explicit claims and determine how current external evidence relates to each claim.

        Use web search for every claim. Prefer primary and official sources whenever possible.
        Do not diagnose the page directly. Do not suggest editorial improvements.

        You will receive:
        - analysisDate: the date of evaluation
        - outputLanguage: the language for human-facing output text
        - url: the page URL
        - pageTitle: the page title
        - contentDate: the best available editorial date for the page, if available
        - claims: 1 to 5 explicit verification requests selected by the diagnosis stage

        Fact-check rules:
        - Convert each claim plus context into a concise search-engine query. Never submit the full request payload as a search query.
        - Research every supplied claim independently and preserve its claimId.
        - Treat minimumSourceTier, requiresCurrentSource, and preferredAuthorityKinds as a deterministic evidence policy supplied by the core. They define what kind of authority and freshness is acceptable; they do not identify a pre-approved URL.
        - If the supplied policy cannot be met, return `unverifiable` rather than accepting a lower-tier source.
        - Answer the claim as written, preserving its material entity, date, number, version, status, and attribution.
        - Determine the claim's original time scope before judging it. If presentedAsCurrent=false, interpret relative wording such as recent, current, currently, now, today, this year, upcoming, new, latest, no announcement, or no confirmed date relative to contentDate and the page's historical context, not relative to analysisDate.
        - If presentedAsCurrent=true, interpret relative wording as current operational guidance as of analysisDate unless the claim itself clearly supplies another time scope.
        - Do not use events, acquisitions, launches, support lifecycle changes, laws, cancellations, or announcements that happened after contentDate to contradict a historical claim. Later evidence may show currentValidity=`outdated`, but it does not by itself make historicalValidity=`contradicted`.
        - For claims about "recent" events, acquisitions, releases, winners, rankings, or support status, constrain the search and judgment to the same time slice implied by contentDate unless the claim is explicitly presented as current.
        - Treat context as hard constraints, not optional hints.
        - When requiresExactScope=true, a contradiction is valid only if a cited source explicitly establishes every supplied context filter for the countervailing fact. Return those filters verbatim in evidenceScope and copy a short exact supporting passage into evidenceQuote. Otherwise return `unverifiable`.
        - For count, award, directory, ranking, or listing claims, apply every material filter simultaneously, such as year, country, category, edition, role, jurisdiction, or version. If the available source cannot be resolved to the same filtered slice, return `unverifiable` rather than forcing a contradiction from a broader result set.
        - For count, award, directory, ranking, listing, or faceted-search claims, never use a search-result snippet, facet count, preview text, or result-card count as final evidence to return `contradicted`. Use it only to discover a source. Contradiction requires source content that explicitly proves the same filtered slice as the claim.
        - A source is not sufficient unless it proves the same entity and materially relevant context. If the evidence is clearly about a different entity, wrong time slice, wrong jurisdiction, wrong version, or wrong category, return `unverifiable` rather than forcing a contradiction.
        - Resolve entity identity before judging a claim. A source about a different organization, product, event, or programme with the same or similar name is irrelevant.
        - For a first-party claim about the page owner, match the source to the page/canonical domain or require unambiguous independent evidence identifying the same organization.
        - A first-party page for an unrelated namesake is not an official source for the entity being checked.
        - For person-specific claims, verify both the named person and the surrounding material qualifiers such as year, category, and count. If one part is missing but appears directly resolvable from the same official source family, keep searching before settling on `partially_supported` or `unverifiable`.
        - Use `supported` when reliable current external evidence supports the claim as presented.
        - Use `contradicted` when reliable evidence shows the proposition is materially false as stated.
        - Use `outdated` when the claim or instruction was previously true or valid but a later change made it no longer current, such as superseded guidance, ended programmes, cancelled events, deprecated products, or changed rules.
        - Use `partially_supported` when the claim is only correct with material conditions, exceptions, scope, or qualifications omitted by the page.
        - Use `unverifiable` when the needed external verification is not strong enough.
        - evidenceRelation describes the relation between the accepted evidence and the claim: supports, contradicts, partially_supports, or insufficient_evidence.
        - factVerdict is the reader-facing factual outcome: verified, false, outdated, partially_verified, insufficient_evidence, or not_a_fact. Keep it consistent with evidenceRelation and the temporal validity fields.
        - The outcome fields are a single contract: `supported` requires evidenceRelation=`supports` and factVerdict=`verified`; `contradicted` requires `contradicts` and `false`; `outdated` requires `contradicts` and `outdated`; `partially_supported` requires `partially_supports` and `partially_verified`; `unverifiable` requires `insufficient_evidence` and `insufficient_evidence`. Do not mix outcomes from different rows.
        - historicalValidity must describe whether the claim was true in its original context: `supported`, `contradicted`, or `unknown`.
        - currentValidity must describe whether the claim remains current as of analysisDate: `supported`, `outdated`, `contradicted`, or `unknown`.
        - When a claim was true in its original context but no longer current now, prefer historicalValidity=`supported` and currentValidity=`outdated`, even if the top-level verdict is otherwise debatable.
        - claimNature, readerActionability, and currentImpact are fixed classification inputs from the claim ledger. Do not reinterpret them. For currentImpact=`update_required`, research the present status even when timeScope=`historical` and presentedAsCurrent=false. If the claim was historically supported but is now outdated or contradicted, return top-level verdict=`outdated` or `contradicted` (not `supported`): the page requires an update because a current reader could still rely on it. For currentImpact=`none`, later changes must not turn a completed historical event into a page defect.
        - If presentedAsCurrent=true and the claim itself contains a date, deadline, or availability period that ended before analysisDate, never return top-level `supported` solely because the historical statement was accurate. Research the current status and return `outdated` or `contradicted` when the reader can no longer act on it.
        - When the page still invites readers to act now on something that has ended, been withdrawn, or been replaced, evaluate that invitation as a current operational claim. If the invitation is no longer valid as of analysisDate, prefer `outdated` or `contradicted` over a weak `partially_supported`.
        - A missing search result is not proof that a claim is false.
        - For `contradicted` or `outdated`, provide at least one reliable source and explain the precise conflict between pageClaim and currentFact.
        - For every verdict other than `unverifiable`, return at least one evidence item. Each evidence item must identify one consulted source URL, its source type, and an exact quote copied from that source. Do not use a quote that you cannot find on the cited URL.
        - Do not return `contradicted`, `outdated`, or `partially_supported` without at least one official or primary source. Keep searching for the responsible government, regulator, organizer, vendor, standards body, certifier, or original publisher. If no such source is available, return `unverifiable`.
        - Source provenance is strict:
          - `official`: published on a domain operated by the government, regulator, organizer, vendor, standards body, certifier, or other entity directly responsible for the fact.
          - `primary`: the original report, dataset, decision, announcement, policy, or documentation published by its author or issuing organization.
          - `authoritative_secondary`: a reputable specialist or major publication accurately interpreting primary material.
          - `secondary`: an independent article, tracker, consultancy, law firm, marketing site, or summary.
          - `community`: a forum, community answer, discussion, or user-generated source.
          - `other`: anything that does not fit the categories above.
        - A third-party site is never `official` or `primary` merely because it quotes legislation, an official announcement, or an original report. Visa trackers, law firms, consultancies, news outlets, and blogs are secondary sources.
        - Sources must be pages actually consulted through web search. Do not invent, reconstruct, or guess URLs.
        - Keep each claim's sources separate. Do not use a source for a different claim unless it directly supports both.
        - Do not put URLs, markdown links, or inline citations inside pageClaim, currentFact, or reason. Return sources only in the sources array.
        - Keep reasons concise and evidence-based.

        Output requirements:
        - Return only valid JSON matching the provided schema.
        - Do not include markdown.
        - Do not include extra properties.
        - Write all human-facing text in outputLanguage.
        - Keep JSON field names and enum values in English.
        """;

    private const string EvidenceEvaluationSystemPrompt = """
        You are an evidence-relation evaluator.

        Do not search the web. Do not rely on outside knowledge. For every supplied claim, decide only how the supplied exact evidence quotes relate to that claim as currently presented at analysisDate.

        Return `supports` only when the quotes affirm or logically entail the material proposition. Return `contradicts` when the quotes negate a material proposition or establish that it is no longer current. Return `partially_supports` when they support only a qualified portion. Return `insufficient_evidence` otherwise.

        A source that merely mentions the same topic is insufficient. A quote that says electric vehicles need vehicle tax contradicts a claim that electric vehicles have zero-rate road tax; it never supports it.
        When presentedAsCurrent=true or currentImpact=update_required, an official quote that abolishes, ends, withdraws, or makes unavailable the programme described by the claim contradicts the claim's current usable status, even when another quote proves that the programme existed historically. In that situation, return `contradicts`, not `insufficient_evidence`.

        Keep JSON field names and enum values in English.
        """;

    private const string EvidenceEvaluationResponseSchemaName = "evidence_relation_evaluation";

    private const string EvidenceEvaluationResponseSchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["checks"],
          "properties": {
            "checks": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["claimId", "relation", "reason"],
                "properties": {
                  "claimId": { "type": "string" },
                  "relation": { "type": "string", "enum": ["supports", "contradicts", "partially_supports", "insufficient_evidence"] },
                  "reason": { "type": "string" }
                }
              }
            }
          }
        }
        """;

    private const string AuthoritativeFactCheckRetrySystemPrompt = FactCheckSystemPrompt + """

        This is the single authoritative-source retry for a claim.
        A previous search found a possible contradiction or later change but did not provide an acceptable official or primary source.
        Search again specifically for the responsible entity's own publication: government or legislation portal, regulator, event organizer, vendor documentation, standards body, certifier, original report publisher, or equivalent first-party authority.
        Do not reuse a third-party summary as official or primary evidence.
        If an official or primary source cannot be found, return `unverifiable`.
        """;

    private const string ExternalEvidenceFactCheckSystemPrompt = FactCheckSystemPrompt + """

        External-evidence mode is active. Do not search the web and do not assume knowledge outside the supplied evidence packet.
        The packet contains Tavily-retrieved content and the only URLs that may be returned in evidence or sources.
        Treat the packet as retrieved material, not as proof by itself: accept only claims whose exact quoted text is present in the cited source URL and satisfies the supplied evidence policy.
        If the packet has no acceptable source for a claim, return `unverifiable`. Do not invent URLs or quotes.
        """;

    private const string FactCheckResponseSchemaName = "page_fact_check";

    private const string FactCheckResponseSchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["summary", "checks"],
          "properties": {
            "summary": { "type": "string" },
            "checks": {
              "type": "array",
              "minItems": 1,
              "maxItems": 3,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["claimId", "verdict", "evidenceRelation", "factVerdict", "confidence", "pageClaim", "currentFact", "reason", "historicalValidity", "currentValidity", "evidenceScope", "evidenceQuote", "evidence", "sources"],
                "properties": {
                  "claimId": { "type": "string" },
                  "verdict": {
                    "type": "string",
                    "enum": ["supported", "contradicted", "outdated", "partially_supported", "unverifiable"]
                  },
                  "evidenceRelation": { "type": "string", "enum": ["supports", "contradicts", "partially_supports", "insufficient_evidence"] },
                  "factVerdict": { "type": "string", "enum": ["verified", "false", "outdated", "partially_verified", "insufficient_evidence", "not_a_fact"] },
                  "confidence": { "type": "number" },
                  "pageClaim": { "type": "string" },
                  "currentFact": { "type": ["string", "null"] },
                  "reason": { "type": "string" },
                  "historicalValidity": {
                    "type": "string",
                    "enum": ["supported", "contradicted", "unknown"]
                  },
                  "currentValidity": {
                    "type": "string",
                    "enum": ["supported", "outdated", "contradicted", "unknown"]
                  },
                  "evidenceScope": {
                    "type": "array",
                    "items": { "type": "string" }
                  },
                  "evidenceQuote": { "type": "string" },
                  "evidence": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["sourceUrl", "sourceType", "quote", "scope"],
                      "properties": {
                        "sourceUrl": { "type": "string" },
                        "sourceType": { "type": "string", "enum": ["official", "primary", "authoritative_secondary", "secondary", "community", "other"] },
                        "quote": { "type": "string" },
                        "scope": { "type": "array", "items": { "type": "string" } }
                      }
                    }
                  },
                  "sources": {
                    "type": "array",
                    "minItems": 0,
                    "maxItems": 5,
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["title", "url", "publisher", "sourceType"],
                      "properties": {
                        "title": { "type": "string" },
                        "url": { "type": "string" },
                        "publisher": { "type": "string" },
                        "sourceType": {
                          "type": "string",
                          "enum": ["official", "primary", "authoritative_secondary", "secondary", "community", "other"]
                        }
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private const string RecommendationSystemPrompt = """
        You are a senior editorial improvement writer.
        Your task is to turn a fixed diagnosis into practical editorial guidance.

        This is not a diagnosis stage.
        Do not reclassify the page.
        Do not debate the evidence.
        Do not perform SEO analysis.
        Do not invent facts.

        You will receive:
        - url: the page URL
        - pageTitle: the page title
        - metadataDescription: the page meta description
        - pageLanguage: the page language, if available
        - contentDate: the best available editorial date for the page, if available
        - fixedIntent: the already-decided page intent
        - fixedIntentRationale: short explanation of why that intent was chosen
        - pageSummary: a concise neutral summary of what the page is about
        - classification: the fixed diagnosis classification (`healthy` or `unhealthy`)
        - issues: the fixed diagnosis issues array
        - classificationConfidence: the diagnosis confidence from 0 to 1
        - insufficientEvidence: whether the diagnosis had limited evidence
        - classificationReasons: 1 to 4 short reasons explaining why the fixed diagnosis was chosen
        - issueTitle: the fixed issue title, when present
        - issueWhyFlagged: the fixed issue explanation, when present
        - evidenceSnippets: 1 to 3 fixed evidence snippets, when present
        - pageContent: the cleaned page content in markdown format

        Use the fixed diagnosis as true.
        Use the page content only to make the recommendation concrete and well targeted.
        If evidenceSnippets are present, treat them as the primary anchors.
        The output may represent either a corrective recommendation or a lighter suggestion depending on the diagnosis.

        Recommended action type:
        - `no_action_needed`
        - `fix_now`
        - `refresh_update`
        - `contextualize`
        - `keep_as_archive`
        Choose the single best action type before writing the recommendation.

        Issue-to-action rules:
        - Use the full issues array, not a single winning issue.
        - If `verified_current_claim_issue` is present, use `fix_now` for a false current claim and `refresh_update` for an outdated current claim.
        - If `verified_historical_claim_issue` is present, use `fix_now` to correct the proven false historical statement.
        - If `expired_date_or_deadline` is present, use `refresh_update` when the page needs a new cycle or date, otherwise use `fix_now` to remove the invalid action.
        - If `misleading_current_status` is present, use `fix_now` or `refresh_update`, never `keep_as_archive` unless the page's fixed intent is genuinely historical.

        Recommendation rules:
        - When the action is corrective, the summary must say what should change and why that change improves the page.
        - When the action is `no_action_needed`, the summary must clearly say the page is healthy and that the steps are optional suggestions, not required fixes.
        - Steps must be concrete, editorial, and executable.
        - Do not produce filler steps.
        - Do not merely paraphrase the diagnosis.
        - If the problem is structural, recommend structural change rather than sentence-level polish.
        - If the problem is missing context, evidence, or ordering, do not force a rewrite.
        - Match the action type to the recommendation:
          - `no_action_needed`: no substantial editorial change is needed
          - `fix_now`: the page needs direct correction
          - `refresh_update`: the page needs current facts, wording, or framing refreshed
          - `contextualize`: the page needs added context, caveats, attribution, or framing
          - `keep_as_archive`: the page should remain available mainly as historical material with clearer framing
        - Use `keep_as_archive` when a dated announcement, recap, or historical page remains truthful but would benefit from archival framing rather than factual updating.
        - Prefer `keep_as_archive` over `refresh_update` for valid historical announcements that should remain published as historical records.

        `exampleRewrite` rules:
        - Return `null` unless rewriting text would materially reduce the diagnosed risk.
        - Use it only when the issue is tied to the wording of a specific snippet.
        - `before` must quote current text exactly.
        - `after` must be publication-ready and must not invent facts, dates, prices, legal claims, statistics, or guarantees.
        - If the existing wording is already correct and the problem is framing, structure, context, or emphasis, return `exampleRewrite: null`.

        Healthy pages:
        - Say that no editorial action is needed.
        - Set `recommendation.mode` to `suggestion`.
        - Still provide at least 2 light maintenance or value-add steps.
        - Make those steps optional and non-corrective.
        - Keep `exampleRewrite` null.

        Non-healthy pages:
        - Set `recommendation.mode` to `recommendation`.

        Output requirements:
        - Return only valid JSON matching the provided schema.
        - Do not include markdown.
        - Do not include extra properties.
        - Write all human-facing text in `outputLanguage`.
        - Keep JSON field names in English.
        """;

    private const string RecommendationResponseSchemaName = "page_recommendation";

    private const string RecommendationResponseSchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["recommendedAction", "recommendation"],
          "properties": {
            "recommendedAction": {
              "type": "object",
              "additionalProperties": false,
              "required": ["type"],
              "properties": {
                "type": {
                  "type": "string",
                  "enum": [
                    "no_action_needed",
                    "fix_now",
                    "refresh_update",
                    "contextualize",
                    "keep_as_archive"
                  ]
                }
              }
            },
            "recommendation": {
              "type": "object",
              "additionalProperties": false,
              "required": ["mode", "summary", "steps", "exampleRewrite"],
              "properties": {
                "mode": {
                  "type": "string",
                  "enum": ["recommendation", "suggestion"]
                },
                "summary": { "type": "string" },
                "steps": {
                  "type": "array",
                  "minItems": 2,
                  "maxItems": 5,
                  "items": { "type": "string" }
                },
                "exampleRewrite": {
                  "type": ["object", "null"],
                  "additionalProperties": false,
                  "required": ["before", "after"],
                  "properties": {
                    "before": { "type": "string" },
                    "after": { "type": "string" }
                  }
                }
              }
            }
          }
        }
        """;

    private static string BuildIntentPrompt(
        string url,
        string finalUrl,
        MetadataResult metadata,
        CaptureResult capture,
        StructuralOutlineResult structuralOutline,
        string outputLanguage)
    {
        _ = finalUrl;
        var sb = new StringBuilder(8000);
        sb.AppendLine($"outputLanguage: {outputLanguage}");
        sb.AppendLine($"url: {url}");
        sb.AppendLine($"canonicalUrl: {(string.IsNullOrWhiteSpace(metadata.CanonicalUrl) ? "<none>" : metadata.CanonicalUrl)}");
        sb.AppendLine($"pageTitle: {metadata.Title ?? "<none>"}");
        var contentDate = metadata.ModifiedDate
            ?? metadata.PublishedDate
            ?? "<none>";
        sb.AppendLine($"contentDate: {contentDate}");
        sb.AppendLine($"pageWordCount: {CountWords(structuralOutline.Markdown)}");
        sb.AppendLine($"metadataDescription: {metadata.MetaDescription ?? "<none>"}");
        sb.AppendLine($"metadataAuthor: {metadata.Author ?? "<none>"}");
        sb.AppendLine();
        sb.AppendLine("pageContent:");
        sb.AppendLine(structuralOutline.Markdown);
        return sb.ToString();
    }

    private static string BuildDiagnosisPrompt(
        string url,
        string finalUrl,
        MetadataResult metadata,
        CaptureResult capture,
        StructuralOutlineResult structuralOutline,
        string outputLanguage,
        JsonElement intent,
        JsonElement temporalGrounding)
    {
        _ = finalUrl;
        _ = capture;
        var sb = new StringBuilder(14000);
        sb.AppendLine($"outputLanguage: {outputLanguage}");
        sb.AppendLine($"analysisDate: {DateTime.UtcNow:yyyy-MM-dd}");
        sb.AppendLine($"url: {url}");
        sb.AppendLine($"canonicalUrl: {(string.IsNullOrWhiteSpace(metadata.CanonicalUrl) ? "<none>" : metadata.CanonicalUrl)}");
        sb.AppendLine($"pageTitle: {metadata.Title ?? "<none>"}");
        sb.AppendLine($"metadataDescription: {metadata.MetaDescription ?? "<none>"}");
        sb.AppendLine($"pageLanguage: {metadata.Language ?? "<none>"}");
        var contentDate = metadata.ModifiedDate
            ?? metadata.PublishedDate
            ?? "<none>";
        sb.AppendLine($"contentDate: {contentDate}");
        sb.AppendLine($"fixedIntent: {ReadJsonString(intent, "intent", "type") ?? "<none>"}");
        sb.AppendLine($"fixedIntentRationale: {ReadJsonString(intent, "intent", "rationale") ?? "<none>"}");
        sb.AppendLine("fixedTemporalGrounding:");
        sb.AppendLine(temporalGrounding.GetRawText());
        sb.AppendLine();
        sb.AppendLine("pageContent:");
        sb.AppendLine(structuralOutline.Markdown);
        return sb.ToString();
    }

    private static string BuildTemporalGroundingPrompt(
        string url,
        string finalUrl,
        MetadataResult metadata,
        CaptureResult capture,
        StructuralOutlineResult structuralOutline,
        string outputLanguage,
        JsonElement intent)
    {
        _ = finalUrl;
        _ = capture;
        var sb = new StringBuilder(12000);
        sb.AppendLine($"outputLanguage: {outputLanguage}");
        sb.AppendLine($"analysisDate: {DateTime.UtcNow:yyyy-MM-dd}");
        sb.AppendLine($"url: {url}");
        sb.AppendLine($"canonicalUrl: {(string.IsNullOrWhiteSpace(metadata.CanonicalUrl) ? "<none>" : metadata.CanonicalUrl)}");
        sb.AppendLine($"pageTitle: {metadata.Title ?? "<none>"}");
        sb.AppendLine($"metadataDescription: {metadata.MetaDescription ?? "<none>"}");
        sb.AppendLine($"pageLanguage: {metadata.Language ?? "<none>"}");
        sb.AppendLine($"contentDate: {metadata.ModifiedDate ?? metadata.PublishedDate ?? "<none>"}");
        sb.AppendLine($"fixedIntent: {ReadJsonString(intent, "intent", "type") ?? "<none>"}");
        sb.AppendLine($"fixedIntentRationale: {ReadJsonString(intent, "intent", "rationale") ?? "<none>"}");
        sb.AppendLine();
        sb.AppendLine("pageContent:");
        sb.AppendLine(structuralOutline.Markdown);
        return sb.ToString();
    }

    private static string BuildVerificationSelectionPrompt(
        string url,
        string finalUrl,
        MetadataResult metadata,
        StructuralOutlineResult structuralOutline,
        string outputLanguage,
        JsonElement intent,
        JsonElement temporalGrounding)
    {
        var sb = new StringBuilder(16000);
        sb.AppendLine($"outputLanguage: {outputLanguage}");
        sb.AppendLine($"analysisDate: {DateTime.UtcNow:yyyy-MM-dd}");
        sb.AppendLine($"url: {url}");
        sb.AppendLine($"finalUrl: {finalUrl}");
        sb.AppendLine($"canonicalUrl: {(string.IsNullOrWhiteSpace(metadata.CanonicalUrl) ? "<none>" : metadata.CanonicalUrl)}");
        sb.AppendLine($"pageTitle: {metadata.Title ?? "<none>"}");
        sb.AppendLine($"contentDate: {metadata.ModifiedDate ?? metadata.PublishedDate ?? "<none>"}");
        sb.AppendLine($"fixedIntent: {ReadJsonString(intent, "intent", "type") ?? "<none>"}");
        sb.AppendLine("temporalAssessment:");
        sb.AppendLine(temporalGrounding.TryGetProperty("temporalAssessment", out var temporalAssessment)
            ? temporalAssessment.GetRawText()
            : "<none>");
        sb.AppendLine();
        sb.AppendLine("pageContent:");
        sb.AppendLine(structuralOutline.Markdown);
        return sb.ToString();
    }

    private static string BuildEvidenceEvaluationPrompt(
        JsonElement checks,
        IReadOnlyList<JsonElement> requestedClaims)
    {
        var sb = new StringBuilder(4000);
        var requestsById = requestedClaims
            .Select(request => new { Id = ReadJsonString(request, "id"), Request = request })
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .ToDictionary(item => item.Id!, item => item.Request, StringComparer.Ordinal);
        sb.AppendLine($"analysisDate: {DateTime.UtcNow:yyyy-MM-dd}");
        sb.AppendLine("checks:");
        foreach (var check in checks.EnumerateArray())
        {
            var claimId = ReadJsonString(check, "claimId") ?? "<none>";
            requestsById.TryGetValue(claimId, out var request);
            sb.AppendLine($"- claimId: {claimId}");
            sb.AppendLine($"  claim: {ReadJsonString(check, "pageClaim") ?? "<none>"}");
            sb.AppendLine($"  proposedVerdict: {ReadJsonString(check, "verdict") ?? "unverifiable"}");
            sb.AppendLine($"  presentedAsCurrent: {ReadJsonBool(request, "presentedAsCurrent")?.ToString() ?? "<none>"}");
            sb.AppendLine($"  timeScope: {ReadJsonString(request, "timeScope") ?? "<none>"}");
            sb.AppendLine($"  claimNature: {ReadJsonString(request, "claimNature") ?? "<none>"}");
            sb.AppendLine($"  readerActionability: {ReadJsonString(request, "readerActionability") ?? "<none>"}");
            sb.AppendLine($"  currentImpact: {ReadJsonString(request, "currentImpact") ?? "<none>"}");
            sb.AppendLine($"  historicalValidity: {ReadJsonString(check, "historicalValidity") ?? "unknown"}");
            sb.AppendLine($"  currentValidity: {ReadJsonString(check, "currentValidity") ?? "unknown"}");
            sb.AppendLine($"  currentFact: {ReadJsonString(check, "currentFact") ?? "<none>"}");
            sb.AppendLine("  evidence:");
            if (check.TryGetProperty("evidence", out var evidence) && evidence.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in evidence.EnumerateArray())
                {
                    sb.AppendLine($"    - quote: {ReadJsonString(item, "quote") ?? "<none>"}");
                }
            }
        }
        return sb.ToString();
    }

    private static string BuildFactCheckPrompt(
        string url,
        MetadataResult metadata,
        string outputLanguage,
        IReadOnlyList<JsonElement> verificationRequests,
        string? externalEvidence = null)
    {
        var sb = new StringBuilder(6000);
        sb.AppendLine($"outputLanguage: {outputLanguage}");
        sb.AppendLine($"analysisDate: {DateTime.UtcNow:yyyy-MM-dd}");
        sb.AppendLine($"url: {url}");
        sb.AppendLine($"pageTitle: {metadata.Title ?? "<none>"}");
        var contentDate = metadata.ModifiedDate
            ?? metadata.PublishedDate
            ?? "<none>";
        sb.AppendLine($"contentDate: {contentDate}");
        sb.AppendLine("claims:");
        foreach (var request in verificationRequests)
        {
            var evidencePolicy = EvidencePolicies.Resolve(
                EvidencePolicies.ParseClaimType(ReadJsonString(request, "claimType")),
                EvidencePolicies.ParseTimeScope(ReadJsonString(request, "timeScope")),
                EvidencePolicies.ParseRisk(ReadJsonString(request, "risk")));
            sb.AppendLine($"- claimId: {ReadJsonString(request, "id") ?? "<none>"}");
            sb.AppendLine($"  claim: {ReadJsonString(request, "claim") ?? "<none>"}");
            sb.AppendLine($"  pageQuote: {ReadJsonString(request, "pageQuote") ?? "<none>"}");
            sb.AppendLine($"  atomicClaim: {ReadJsonString(request, "atomicClaim") ?? "<none>"}");
            sb.AppendLine($"  context: {string.Join("; ", ReadJsonArrayValues(request, "context"))}");
            sb.AppendLine($"  scope: {string.Join("; ", ReadJsonArrayValues(request, "scope"))}");
            sb.AppendLine($"  presentedAsCurrent: {ReadJsonBool(request, "presentedAsCurrent")?.ToString() ?? "<none>"}");
            sb.AppendLine($"  requiresExactScope: {ReadJsonBool(request, "requiresExactScope")?.ToString() ?? "<none>"}");
            sb.AppendLine($"  claimType: {ReadJsonString(request, "claimType") ?? "other"}");
            sb.AppendLine($"  timeScope: {ReadJsonString(request, "timeScope") ?? "timeless"}");
            sb.AppendLine($"  volatility: {ReadJsonString(request, "volatility") ?? "stable"}");
            sb.AppendLine($"  risk: {ReadJsonString(request, "risk") ?? "standard"}");
            sb.AppendLine($"  claimNature: {ReadJsonString(request, "claimNature") ?? "current_state"}");
            sb.AppendLine($"  readerActionability: {ReadJsonString(request, "readerActionability") ?? "informational"}");
            sb.AppendLine($"  currentImpact: {ReadJsonString(request, "currentImpact") ?? "context_needed"}");
            sb.AppendLine($"  minimumSourceTier: {evidencePolicy.MinimumSourceTier}");
            sb.AppendLine($"  requiresCurrentSource: {evidencePolicy.RequiresCurrentSource}");
            sb.AppendLine($"  preferredAuthorityKinds: {string.Join(", ", evidencePolicy.PreferredAuthorityKinds)}");
            sb.AppendLine($"  whyMaterial: {ReadJsonString(request, "whyMaterial") ?? "<none>"}");
        }
        if (!string.IsNullOrWhiteSpace(externalEvidence))
        {
            sb.AppendLine();
            sb.AppendLine("externalEvidence:");
            sb.AppendLine(externalEvidence);
        }
        return sb.ToString();
    }

    private static string BuildRecommendationPrompt(
        string url,
        string finalUrl,
        MetadataResult metadata,
        CaptureResult capture,
        StructuralOutlineResult structuralOutline,
        string outputLanguage,
        JsonElement intent,
        JsonElement diagnosis)
    {
        _ = finalUrl;
        _ = capture;
        var sb = new StringBuilder(16000);
        sb.AppendLine($"outputLanguage: {outputLanguage}");
        sb.AppendLine($"url: {url}");
        sb.AppendLine($"pageTitle: {metadata.Title ?? "<none>"}");
        sb.AppendLine($"metadataDescription: {metadata.MetaDescription ?? "<none>"}");
        sb.AppendLine($"pageLanguage: {metadata.Language ?? "<none>"}");
        var contentDate = metadata.ModifiedDate
            ?? metadata.PublishedDate
            ?? "<none>";
        sb.AppendLine($"contentDate: {contentDate}");
        sb.AppendLine($"fixedIntent: {ReadJsonString(intent, "intent", "type") ?? "<none>"}");
        sb.AppendLine($"fixedIntentRationale: {ReadJsonString(intent, "intent", "rationale") ?? "<none>"}");
        sb.AppendLine($"pageSummary: {ReadJsonString(intent, "pageSummary") ?? "<none>"}");
        sb.AppendLine($"classification: {ReadJsonString(diagnosis, "classification", "type") ?? "<none>"}");
        sb.AppendLine($"issues: {string.Join("; ", ReadIssues(diagnosis))}");
        sb.AppendLine($"classificationConfidence: {ReadJsonDouble(diagnosis, "classification", "confidence")?.ToString("0.###") ?? "<none>"}");
        sb.AppendLine($"insufficientEvidence: {ReadJsonBool(diagnosis, "classification", "insufficientEvidence")?.ToString() ?? "<none>"}");
        sb.AppendLine("classificationReasons:");
        if (diagnosis.TryGetProperty("classificationReasons", out var classificationReasons)
            && classificationReasons.ValueKind == JsonValueKind.Array
            && classificationReasons.GetArrayLength() > 0)
        {
            foreach (var reason in classificationReasons.EnumerateArray())
            {
                sb.AppendLine($"- {reason.GetString() ?? "<none>"}");
            }
        }
        else
        {
            sb.AppendLine("- <none>");
        }
        sb.AppendLine($"issueTitle: {ReadJsonString(diagnosis, "issue", "title") ?? "<none>"}");
        sb.AppendLine($"issueWhyFlagged: {ReadJsonString(diagnosis, "issue", "whyFlagged") ?? "<none>"}");
        sb.AppendLine("factCheckFindings:");
        if (diagnosis.TryGetProperty("factChecks", out var factChecks) && factChecks.ValueKind == JsonValueKind.Array && factChecks.GetArrayLength() > 0)
        {
            foreach (var check in factChecks.EnumerateArray())
            {
                sb.AppendLine($"- verdict: {ReadJsonString(check, "verdict") ?? "<none>"}");
                sb.AppendLine($"  pageClaim: {ReadJsonString(check, "pageClaim") ?? "<none>"}");
                sb.AppendLine($"  currentFact: {ReadJsonString(check, "currentFact") ?? "<none>"}");
                sb.AppendLine($"  reason: {ReadJsonString(check, "reason") ?? "<none>"}");
                sb.AppendLine($"  historicalValidity: {ReadJsonString(check, "historicalValidity") ?? "<none>"}");
                sb.AppendLine($"  currentValidity: {ReadJsonString(check, "currentValidity") ?? "<none>"}");
            }
        }
        else
        {
            sb.AppendLine("- <none>");
        }
        sb.AppendLine("evidenceSnippets:");

        if (diagnosis.TryGetProperty("issue", out var issue) && issue.ValueKind == JsonValueKind.Object)
        {
            if (issue.TryGetProperty("evidenceSnippets", out var snippets) && snippets.ValueKind == JsonValueKind.Array)
            {
                var hasAny = false;
                foreach (var snippet in snippets.EnumerateArray())
                {
                    hasAny = true;
                    sb.AppendLine($"- text: {ReadJsonString(snippet, "text") ?? "<none>"}");
                    sb.AppendLine("  context: <none>");
                }

                if (!hasAny)
                {
                    sb.AppendLine("- <none>");
                }
            }
            else
            {
                sb.AppendLine("- <none>");
            }
        }
        else
        {
            sb.AppendLine("- <none>");
        }

        sb.AppendLine();
        sb.AppendLine("pageContent:");
        sb.AppendLine(structuralOutline.Markdown);
        return sb.ToString();
    }

    private static async Task<IDocument> ParseDocumentAsync(string html)
    {
        var context = BrowsingContext.New(Configuration.Default);
        return await context.OpenAsync(req => req.Content(html));
    }

    private static async Task<StructuralOutlineResult> BuildStructuralOutlineAsync(IDocument document)
    {
        var container = await CreateLightSanitizedContainerAsync(document);
        var pageRootSelector = BuildElementSelector(container);
        var blocks = new List<string> { "<page>" };
        var regions = ExtractPageRegions(container);

        foreach (var region in regions)
        {
            blocks.Add($"<{region.Name}>");
            RenderPageRegion(region, blocks);
            blocks.Add($"</{region.Name}>");
        }

        blocks.Add("</page>");

        var limitedBlocks = new List<string>();
        var estimatedTokens = 0;
        var truncatedByTextLimit = false;

        foreach (var block in blocks)
        {
            var lineTokens = EstimateTokenCount(block + Environment.NewLine + Environment.NewLine);
            if (estimatedTokens + lineTokens > AppConstants.StructuralOutlineMaxEstimatedTokens)
            {
                truncatedByTextLimit = true;
                break;
            }

            limitedBlocks.Add(block);
            estimatedTokens += lineTokens;
        }

        return new StructuralOutlineResult
        {
            Summary = new StructuralOutlineSummary
            {
                EstimatedTokens = estimatedTokens,
                TruncatedByTextLimit = truncatedByTextLimit
            },
            Markdown = BuildSemanticMarkdown(limitedBlocks, truncatedByTextLimit)
        };
    }

    private static async Task<int> GetRenderedWordCountAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<int>(
                "() => {" +
                "const text = document.body ? (document.body.innerText || '') : '';" +
                "return text.trim().split(/\\s+/).filter(Boolean).length;" +
                "}");
        }
        catch (PlaywrightException)
        {
            return 0;
        }
    }

    private static async Task<string[]> ExtractHeadingsAsync(IPage page, string selector)
    {
        try
        {
            return await page.EvaluateAsync<string[]>(
            @"(sel) => {
                const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
                const seen = new Set();
                const result = [];
                for (const node of document.querySelectorAll(sel)) {
                    const value = normalize(node.textContent || '');
                    if (!value) continue;
                    const key = value.toLowerCase();
                    if (seen.has(key)) continue;
                    seen.add(key);
                    result.push(value);
                    if (result.length >= 20) break;
                }
                return result;
            }",
            selector);
        }
        catch (PlaywrightException)
        {
            return [];
        }
    }

    private static string ResolveChromiumExecutablePath()
    {
        foreach (var candidate in GetChromiumExecutableCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> GetChromiumExecutableCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                yield return Path.Combine(programFiles, "Chromium", "Application", "chrome.exe");
                yield return Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe");
            }

            if (!string.IsNullOrWhiteSpace(programFilesX86))
            {
                yield return Path.Combine(programFilesX86, "Chromium", "Application", "chrome.exe");
                yield return Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe");
            }

            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                yield return Path.Combine(localAppData, "Chromium", "Application", "chrome.exe");
                yield return Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe");
            }
        }

        if (OperatingSystem.IsLinux())
        {
            yield return "/usr/bin/google-chrome-stable";
            yield return "/usr/bin/google-chrome";
            yield return "/usr/bin/chromium-browser";
            yield return "/usr/bin/chromium";
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Chromium.app/Contents/MacOS/Chromium";
            yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
        }
    }

    private static string ExtractOpenAiContent(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        var message = choices[0].GetProperty("message");
        return message.TryGetProperty("content", out var content) ? content.GetString() ?? string.Empty : string.Empty;
    }

    private static JsonElement? TryParseJson(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string CreateOutputDirectory(Uri pageUri)
    {
        var slug = Regex.Replace(pageUri.Host + pageUri.AbsolutePath, @"[^A-Za-z0-9]+", "-")
            .Trim('-')
            .ToLowerInvariant();
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(AppConstants.OutputRoot, $"{stamp}-{slug}");
    }

    private static string InferUrlPatternHint(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return "/";
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? "/" : $"/{segments[0].ToLowerInvariant()}";
    }

    private static string? ExtractSiteNameFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var separators = new[] { " | ", " — ", " - " };
        foreach (var separator in separators)
        {
            var parts = title.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                return parts[^1];
            }
        }

        return null;
    }

    private static string NormalizeWhitespace(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant).Trim();

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static int CountWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return Regex.Count(text, @"\b[\p{L}\p{N}][\p{L}\p{N}'’\-]*\b", RegexOptions.CultureInvariant);
    }

    private static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static async Task<IElement> CreateLightSanitizedContainerAsync(IDocument document)
    {
        var context = BrowsingContext.New(Configuration.Default);
        var fragmentDocument = await context.OpenNewAsync();
        var container = fragmentDocument.CreateElement("div");
        container.InnerHtml = document.Body?.InnerHtml ?? string.Empty;

        foreach (var selector in NoiseSelectors)
        {
            foreach (var element in container.QuerySelectorAll(selector))
            {
                element.Remove();
            }
        }

        foreach (var element in container.QuerySelectorAll("*").OfType<IElement>().ToArray())
        {
            if (ShouldRemoveAsObviousNoise(element))
            {
                element.Remove();
            }
        }

        return container;
    }

    private static bool ShouldRemoveAsObviousNoise(IElement element)
    {
        if (element.TagName is "SCRIPT" or "STYLE" or "NOSCRIPT" or "IFRAME" or "SVG" or "CANVAS")
        {
            return true;
        }

        var combined = $"{element.Id} {element.ClassName} {element.GetAttribute("role")} {element.GetAttribute("aria-hidden")}".Trim();
        if (Regex.IsMatch(
                combined,
                @"(?:^|\s|-)(?:cookie|cookies|popup|modal|overlay|captcha|recaptcha|grecaptcha|sr-only|owl-clone|carouselTicker__clone)(?:$|\s|-)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (string.Equals(element.GetAttribute("aria-hidden"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var style = element.GetAttribute("style") ?? string.Empty;
        return style.Contains("display:none", StringComparison.OrdinalIgnoreCase)
            || style.Contains("visibility:hidden", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<PageRegion> ExtractPageRegions(IElement container)
    {
        var regions = new List<PageRegion>();

        foreach (var child in container.Children.OfType<IElement>())
        {
            AppendPageRegions(child, regions);
        }

        if (regions.Count == 0 && HasMeaningfulRegionContent(container))
        {
            regions.Add(new PageRegion
            {
                Name = ClassifyPageRegion(container),
                Element = container
            });
        }

        return regions;
    }

    private static void AppendPageRegions(IElement element, List<PageRegion> regions)
    {
        if (ShouldRemoveAsObviousNoise(element))
        {
            return;
        }

        if (ShouldPromoteChildrenAsRegions(element))
        {
            foreach (var child in element.Children.OfType<IElement>())
            {
                AppendPageRegions(child, regions);
            }

            return;
        }

        if (!HasMeaningfulRegionContent(element))
        {
            foreach (var child in element.Children.OfType<IElement>())
            {
                AppendPageRegions(child, regions);
            }

            return;
        }

        regions.Add(new PageRegion
        {
            Name = ClassifyPageRegion(element),
            Element = element
        });
    }

    private static bool ShouldPromoteChildrenAsRegions(IElement element)
    {
        if (element.TagName is not ("DIV" or "SECTION" or "MAIN" or "ARTICLE" or "BODY"))
        {
            return false;
        }

        if (LooksLikeBoilerplateContainer(element)
            || LooksLikeNavigationContainer(element)
            || LooksLikeWidgetContainer(element)
            || LooksLikeHeroRegion(element)
            || LooksLikeLogoGridRegion(element)
            || LooksLikeContactDetailsContainer(element)
            || LooksLikeContactDetailsContainer(element))
        {
            return false;
        }

        var meaningfulChildren = element.Children
            .OfType<IElement>()
            .Where(child => !ShouldRemoveAsObviousNoise(child))
            .Where(HasMeaningfulRegionContent)
            .ToArray();

        if (meaningfulChildren.Length < 2)
        {
            return false;
        }

        var blockChildCount = meaningfulChildren.Count(child =>
            child.TagName is "SECTION" or "ARTICLE" or "MAIN" or "NAV" or "ASIDE" or "FOOTER" or "FORM");
        var childHeadingCount = meaningfulChildren.Count(child =>
            child.QuerySelector("h1, h2, h3, h4, h5, h6") is not null);

        return blockChildCount >= 2 || childHeadingCount >= 2;
    }

    private static string ClassifyPageRegion(IElement element)
    {
        if (element.TagName.Equals("NAV", StringComparison.OrdinalIgnoreCase))
        {
            return "navigation";
        }

        if (element.TagName.Equals("FOOTER", StringComparison.OrdinalIgnoreCase))
        {
            return "footer";
        }

        if (element.TagName.Equals("ASIDE", StringComparison.OrdinalIgnoreCase))
        {
            return "sidebar";
        }

        if (element.TagName.Equals("FORM", StringComparison.OrdinalIgnoreCase))
        {
            return "form";
        }

        if (LooksLikeNavigationContainer(element))
        {
            return "navigation";
        }

        if (LooksLikeBoilerplateContainer(element))
        {
            return "boilerplate";
        }

        if (LooksLikeWidgetContainer(element))
        {
            return "widget";
        }

        if (LooksLikeContactDetailsContainer(element))
        {
            return "contact_details";
        }

        if (LooksLikeHeroRegion(element))
        {
            return "hero";
        }

        if (LooksLikeLogoGridRegion(element))
        {
            return "logo_grid";
        }

        return "content";
    }

    private static void RenderPageRegion(PageRegion region, List<string> blocks)
    {
        if (region.Name is "navigation")
        {
            var links = region.Element.QuerySelectorAll("a[href]")
                .OfType<IElement>()
                .Select(anchor => new
                {
                    Text = NormalizeWhitespace(anchor.TextContent),
                    Href = NormalizeWhitespace(anchor.GetAttribute("href"))
                })
                .Where(link => !string.IsNullOrWhiteSpace(link.Text) || !string.IsNullOrWhiteSpace(link.Href))
                .DistinctBy(link => $"{link.Text}|{link.Href}", StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToArray();

            foreach (var link in links)
            {
                AppendSemanticBlock(blocks, string.IsNullOrWhiteSpace(link.Href)
                    ? $"- {link.Text}"
                    : $"- {RenderLinkTag(link.Text, link.Href)}");
            }

            return;
        }

        RenderSemanticElement(region.Element, blocks);
    }

    private static bool HasMeaningfulRegionContent(IElement element)
    {
        if (ShouldRemoveAsObviousNoise(element))
        {
            return false;
        }

        if (LooksLikeBoilerplateContainer(element))
        {
            return true;
        }

        if (element.QuerySelector("h1, h2, h3, h4, h5, h6, p, ul, ol, table, form, blockquote") is not null)
        {
            return true;
        }

        if (element.QuerySelectorAll("img")
            .OfType<IElement>()
            .Any(img => ShouldKeepSemanticImageAlt(img, NormalizeWhitespace(img.GetAttribute("alt")))))
        {
            return true;
        }

        var directText = NormalizeWhitespace(string.Join(
            " ",
            element.ChildNodes
                .Where(node => node.NodeType == NodeType.Text)
                .Select(node => node.TextContent)));
        if (ShouldKeepSemanticText(directText))
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikeNavigationContainer(IElement element)
    {
        if (HasEditorialContentSignals(element))
        {
            return false;
        }

        if (element.TagName.Equals("NAV", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var role = element.GetAttribute("role") ?? string.Empty;
        if (role.Equals("navigation", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var context = $"{element.TagName} {element.Id} {element.ClassName} {element.GetAttribute("aria-label")}";
        if (Regex.IsMatch(
                context,
                @"(?:^|[\s\-_])(?:nav|navbar|navigation|menu|main-menu|breadcrumb|breadcrumbs|pagination)(?:$|[\s\-_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return true;
        }

        return false;
    }

    private static bool HasEditorialContentSignals(IElement element)
    {
        if (element.QuerySelector("article, main") is not null)
        {
            return true;
        }

        var paragraphCount = element.QuerySelectorAll("p").Length;
        var headingCount = element.QuerySelectorAll("h1, h2, h3").Length;
        var textWords = CountWords(NormalizeWhitespace(element.TextContent));

        return paragraphCount >= 3
            || (headingCount >= 1 && paragraphCount >= 1)
            || textWords >= 180;
    }

    private static bool LooksLikeBoilerplateContainer(IElement element)
    {
        var context = $"{element.TagName} {element.Id} {element.ClassName} {element.GetAttribute("role")} {element.GetAttribute("aria-label")}";
        if (Regex.IsMatch(context, @"(?:footer|copyright|legal|terms|privacy)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return true;
        }

        var text = NormalizeWhitespace(element.TextContent);
        return CountWords(text) > 0
            && CountWords(text) <= 30
            && Regex.IsMatch(text, @"(?:privacy|cookie|terms|copyright|all rights reserved|derechos reservados)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeWidgetContainer(IElement element)
    {
        var context = $"{element.TagName} {element.Id} {element.ClassName} {element.GetAttribute("role")} {element.GetAttribute("aria-label")}";
        return Regex.IsMatch(
            context,
            @"(?:widget|carousel|slider|accordion|tablist|tabs|modal|popup|share|social|newsletter|subscribe)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeHeroRegion(IElement element)
    {
        var headingCount = element.QuerySelectorAll("h1").Length;
        if (headingCount == 0)
        {
            return false;
        }

        var wordCount = CountWords(NormalizeWhitespace(element.TextContent));
        return wordCount <= 120;
    }

    private static bool LooksLikeLogoGridRegion(IElement element)
    {
        var semanticImages = element.QuerySelectorAll("img")
            .OfType<IElement>()
            .Count(img => ShouldKeepSemanticImageAlt(img, NormalizeWhitespace(img.GetAttribute("alt"))));
        if (semanticImages < 3)
        {
            return false;
        }

        var wordCount = CountWords(NormalizeWhitespace(element.TextContent));
        return wordCount <= semanticImages * 8;
    }

    private static string BuildElementSelector(IElement element)
    {
        var parts = new List<string> { element.TagName.ToLowerInvariant() };
        if (!string.IsNullOrWhiteSpace(element.Id))
        {
            parts.Add($"#{element.Id}");
        }

        var firstClass = (element.ClassName ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(firstClass))
        {
            parts.Add($".{firstClass}");
        }

        return string.Join(string.Empty, parts);
    }

    private static string BuildSemanticMarkdown(IReadOnlyList<string> nodeBlocks, bool truncatedByTextLimit)
    {
        var sb = new StringBuilder();

        foreach (var block in nodeBlocks)
        {
            if (sb.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
            }

            sb.Append(block);
        }

        if (truncatedByTextLimit)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append("<!-- truncated_by_text_limit -->");
        }

        return sb.ToString().TrimEnd();
    }

    private static void RenderSemanticElement(IElement element, List<string> blocks)
    {
        if (ShouldSkipSemanticElement(element))
        {
            return;
        }

        if (TryRenderSemanticLeaf(element, blocks))
        {
            return;
        }

        if (element.TagName.Equals("BLOCKQUOTE", StringComparison.OrdinalIgnoreCase))
        {
            var innerBlocks = new List<string>();
            RenderSemanticChildren(element, innerBlocks);
            if (innerBlocks.Count == 0)
            {
                var quoteText = ExtractInlineText(element);
                if (ShouldKeepSemanticText(quoteText))
                {
                    AppendSemanticBlock(blocks, $"> {quoteText}");
                }

                return;
            }

            AppendSemanticBlock(blocks, "<blockquote>");
            foreach (var block in innerBlocks)
            {
                AppendSemanticBlock(blocks, block);
            }

            AppendSemanticBlock(blocks, "</blockquote>");
            return;
        }

        if (IsContainerSection(element))
        {
            AppendSemanticBlock(blocks, "<section>");
            RenderSemanticChildren(element, blocks);
            AppendSemanticBlock(blocks, "</section>");
            return;
        }

        RenderSemanticChildren(element, blocks);
    }

    private static void RenderSemanticChildren(IElement element, List<string> blocks)
    {
        var directTextBuffer = new List<string>();

        foreach (var childNode in element.ChildNodes)
        {
            if (childNode.NodeType == NodeType.Text)
            {
                var text = NormalizeWhitespace(childNode.TextContent);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    directTextBuffer.Add(text);
                }

                continue;
            }

            if (childNode is not IElement childElement)
            {
                continue;
            }

            FlushDirectTextBuffer(directTextBuffer, blocks);
            RenderSemanticElement(childElement, blocks);
        }

        FlushDirectTextBuffer(directTextBuffer, blocks);
    }

    private static void FlushDirectTextBuffer(List<string> directTextBuffer, List<string> blocks)
    {
        if (directTextBuffer.Count == 0)
        {
            return;
        }

        var combined = NormalizeWhitespace(string.Join(" ", directTextBuffer));
        directTextBuffer.Clear();
        AppendSemanticBlock(blocks, combined);
    }

    private static bool TryRenderSemanticLeaf(IElement element, List<string> blocks)
    {
        if (TryRenderHeading(element, blocks))
        {
            return true;
        }

        if (TryRenderParagraph(element, blocks))
        {
            return true;
        }

        if (TryRenderList(element, blocks))
        {
            return true;
        }

        if (TryRenderTable(element, blocks))
        {
            return true;
        }

        if (TryRenderForm(element, blocks))
        {
            return true;
        }

        if (TryRenderContactDetails(element, blocks))
        {
            return true;
        }

        if (TryRenderImageAlt(element, blocks))
        {
            return true;
        }

        return false;
    }

    private static bool TryRenderHeading(IElement element, List<string> blocks)
    {
        if (!Regex.IsMatch(element.TagName, @"^H([1-6])$", RegexOptions.CultureInvariant))
        {
            return false;
        }

        var headingText = NormalizeWhitespace(element.TextContent);
        if (!ShouldKeepSemanticText(headingText))
        {
            return false;
        }

        var level = int.Parse(element.TagName[1..], System.Globalization.CultureInfo.InvariantCulture);
        AppendSemanticBlock(blocks, $"{new string('#', Math.Clamp(level, 1, 6))} {headingText}");
        return true;
    }

    private static bool TryRenderParagraph(IElement element, List<string> blocks)
    {
        if (element.TagName is not ("P" or "LI"))
        {
            return false;
        }

        var text = ExtractInlineText(element);
        if (!ShouldKeepSemanticText(text))
        {
            return false;
        }

        AppendSemanticBlock(blocks, element.TagName.Equals("LI", StringComparison.OrdinalIgnoreCase)
            ? $"- {text}"
            : text);
        return true;
    }

    private static bool TryRenderList(IElement element, List<string> blocks)
    {
        if (element.TagName is not ("UL" or "OL"))
        {
            return false;
        }

        var ordered = element.TagName.Equals("OL", StringComparison.OrdinalIgnoreCase);
        var items = element.Children
            .OfType<IElement>()
            .Where(child => child.TagName.Equals("LI", StringComparison.OrdinalIgnoreCase))
            .Select(child => ExtractInlineText(child))
            .Where(ShouldKeepSemanticText)
            .ToArray();

        if (items.Length == 0)
        {
            return false;
        }

        for (var index = 0; index < items.Length; index++)
        {
            var marker = ordered ? $"{index + 1}." : "-";
            AppendSemanticBlock(blocks, $"{marker} {items[index]}");
        }

        return true;
    }

    private static bool TryRenderTable(IElement element, List<string> blocks)
    {
        if (!element.TagName.Equals("TABLE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var table = RenderSemanticTable(element);
        if (string.IsNullOrWhiteSpace(table))
        {
            return false;
        }

        AppendSemanticBlock(blocks, table);
        return true;
    }

    private static bool TryRenderForm(IElement element, List<string> blocks)
    {
        if (!element.TagName.Equals("FORM", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fieldLabels = element.QuerySelectorAll("input, select, textarea")
            .OfType<IElement>()
            .Select(GetStructuralOutlineFormFieldLabel)
            .Where(ShouldKeepSemanticText)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var consentLabels = element.QuerySelectorAll("input[type='checkbox'], input[type='radio']")
            .OfType<IElement>()
            .Select(GetStructuralOutlineFormConsentLabel)
            .Where(ShouldKeepSemanticText)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var submitLabels = element.QuerySelectorAll("button:not([type]), button[type='submit'], input[type='submit']")
            .OfType<IElement>()
            .Select(GetStructuralOutlineFormActionLabel)
            .Where(ShouldKeepSemanticText)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (fieldLabels.Length == 0 && consentLabels.Length == 0 && submitLabels.Length == 0)
        {
            return false;
        }

        var formBlock = new StringBuilder();
        formBlock.AppendLine("<form>");

        foreach (var label in fieldLabels)
        {
            formBlock.AppendLine($"  <field>{label}</field>");
        }

        foreach (var label in consentLabels)
        {
            formBlock.AppendLine($"  <consent>{label}</consent>");
        }

        foreach (var label in submitLabels)
        {
            formBlock.AppendLine($"  <button type=\"submit\">{label}</button>");
        }

        formBlock.Append("</form>");
        AppendSemanticBlock(blocks, formBlock.ToString());
        return true;
    }

    private static bool TryRenderContactDetails(IElement element, List<string> blocks)
    {
        if (!LooksLikeContactDetailsContainer(element))
        {
            return false;
        }

        var fullText = NormalizeWhitespace(element.TextContent);
        var phones = ExtractStructuralOutlinePhones(fullText);
        var emails = ExtractStructuralOutlineEmails(fullText);
        var addresses = ExtractStructuralOutlineAddressFragments(element);

        if (phones.Length == 0 && emails.Length == 0 && addresses.Length == 0)
        {
            return false;
        }

        var contactBlock = new StringBuilder();
        contactBlock.AppendLine("<contact_details>");

        foreach (var phone in phones)
        {
            contactBlock.AppendLine($"- Phone: {phone}");
        }

        foreach (var email in emails)
        {
            contactBlock.AppendLine($"- Email: {email}");
        }

        foreach (var address in addresses)
        {
            contactBlock.AppendLine($"- Address: {address}");
        }

        contactBlock.Append("</contact_details>");
        AppendSemanticBlock(blocks, contactBlock.ToString());
        return true;
    }

    private static bool TryRenderImageAlt(IElement element, List<string> blocks)
    {
        if (!element.TagName.Equals("IMG", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var alt = NormalizeWhitespace(element.GetAttribute("alt"));
        if (!ShouldKeepSemanticImageAlt(element, alt))
        {
            return false;
        }

        AppendSemanticBlock(blocks, $"[image] {alt}");
        return true;
    }

    private static bool ShouldSkipSemanticElement(IElement element)
    {
        if (ShouldRemoveAsObviousNoise(element))
        {
            return true;
        }

        if (element.TagName is "SCRIPT" or "STYLE" or "NOSCRIPT" or "IFRAME")
        {
            return true;
        }

        if (element.TagName is "NAV" or "FOOTER")
        {
            return true;
        }

        var role = element.GetAttribute("role") ?? string.Empty;
        return Regex.IsMatch(
            role,
            @"(?:navigation|contentinfo|dialog)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsContainerSection(IElement element)
    {
        if (element.TagName is not ("ARTICLE" or "SECTION" or "MAIN" or "DIV"))
        {
            return false;
        }

        return element.Children.OfType<IElement>().Any(child =>
            child.TagName is "H1" or "H2" or "H3" or "P" or "UL" or "OL" or "BLOCKQUOTE" or "TABLE");
    }

    private static void AppendSemanticBlock(List<string> blocks, string? block)
    {
        var normalized = NormalizeWhitespace(block);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (!ShouldKeepSemanticText(normalized)
            && !Regex.IsMatch(normalized, @"^</?[a-z_]+>$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && !normalized.StartsWith("<!--", StringComparison.Ordinal))
        {
            return;
        }

        if (blocks.Count > 0 && string.Equals(blocks[^1], normalized, StringComparison.Ordinal))
        {
            return;
        }

        blocks.Add(normalized);
    }

    private static bool ShouldKeepSemanticText(string? text)
    {
        var normalized = NormalizeWhitespace(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (CountWords(normalized) >= 2)
        {
            return true;
        }

        return Regex.IsMatch(
            normalized,
            @"(?:@|\+\d|\d{4}|\b[A-Z][a-z]+\b)",
            RegexOptions.CultureInvariant);
    }

    private static string ExtractInlineText(IElement element)
    {
        var parts = new List<string>();
        CollectInlineText(element, parts);
        return NormalizeWhitespace(string.Join(" ", parts));
    }

    private static void CollectInlineText(INode node, List<string> parts)
    {
        if (node.NodeType == NodeType.Text)
        {
            var text = NormalizeWhitespace(node.TextContent);
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }

            return;
        }

        if (node is not IElement element || ShouldRemoveAsObviousNoise(element))
        {
            return;
        }

        if (element.TagName.Equals("IMG", StringComparison.OrdinalIgnoreCase))
        {
            var alt = NormalizeWhitespace(element.GetAttribute("alt"));
            if (ShouldKeepSemanticImageAlt(element, alt))
            {
                parts.Add(alt);
            }

            return;
        }

        if (element.TagName.Equals("A", StringComparison.OrdinalIgnoreCase))
        {
            var linkText = NormalizeWhitespace(element.TextContent);
            var href = NormalizeWhitespace(element.GetAttribute("href"));

            if (!string.IsNullOrWhiteSpace(linkText))
            {
                parts.Add(string.IsNullOrWhiteSpace(href)
                    ? linkText
                    : RenderLinkTag(linkText, href));
            }

            return;
        }

        foreach (var childNode in element.ChildNodes)
        {
            CollectInlineText(childNode, parts);
        }
    }

    private static string RenderLinkTag(string text, string href)
    {
        var safeText = EscapeSemanticText(text);
        var safeHref = EscapeSemanticAttribute(href);
        return $"<link href=\"{safeHref}\">{safeText}</link>";
    }

    private static string EscapeSemanticText(string value) =>
        value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string EscapeSemanticAttribute(string value) =>
        EscapeSemanticText(value)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    private static bool LooksLikeContactDetailsContainer(IElement element)
    {
        var fullText = NormalizeWhitespace(element.TextContent);
        if (string.IsNullOrWhiteSpace(fullText))
        {
            return false;
        }

        var phones = ExtractStructuralOutlinePhones(fullText);
        var emails = ExtractStructuralOutlineEmails(fullText);
        var addresses = ExtractStructuralOutlineAddressFragments(element);
        var directSignalCount = phones.Length + emails.Length + addresses.Length;
        if (directSignalCount == 0)
        {
            return false;
        }

        if (element.TagName is "FOOTER" or "ADDRESS")
        {
            return true;
        }

        if (CountWords(fullText) > 120)
        {
            return false;
        }

        var ownContext = $"{element.TagName} {element.Id} {element.ClassName} {element.GetAttribute("role")} {element.GetAttribute("aria-label")}";
        return Regex.IsMatch(
            ownContext,
            @"(?:contact|find-us|findus|office|address|visit|phone|email|footer)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || directSignalCount >= 2;
    }

    private static bool IsStructuralOutlineFormRelatedElement(IElement element)
    {
        if (element.TagName is "FORM" or "LABEL" or "INPUT" or "SELECT" or "TEXTAREA" or "OPTION" or "BUTTON" or "FIELDSET" or "LEGEND")
        {
            return true;
        }

        return element.Closest("form") is not null;
    }

    private static string GetStructuralOutlineFormFieldLabel(IElement field)
    {
        if (!field.TagName.Equals("INPUT", StringComparison.OrdinalIgnoreCase)
            && !field.TagName.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
            && !field.TagName.Equals("TEXTAREA", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var inputType = NormalizeWhitespace(field.GetAttribute("type")).ToLowerInvariant();
        if (inputType is "hidden" or "submit" or "button" or "reset" or "image" or "checkbox" or "radio" or "search")
        {
            return string.Empty;
        }

        var associatedLabel = GetStructuralOutlineAssociatedLabel(field);
        if (ShouldKeepStructuralOutlineFieldLabel(associatedLabel))
        {
            return associatedLabel;
        }

        var placeholder = NormalizeWhitespace(field.GetAttribute("placeholder"));
        if (ShouldKeepStructuralOutlineFieldLabel(placeholder))
        {
            return placeholder;
        }

        var ariaLabel = NormalizeWhitespace(field.GetAttribute("aria-label"));
        if (ShouldKeepStructuralOutlineFieldLabel(ariaLabel))
        {
            return ariaLabel;
        }

        var name = NormalizeWhitespace(field.GetAttribute("name"));
        if (ShouldKeepStructuralOutlineFieldLabel(name))
        {
            return name;
        }

        return string.Empty;
    }

    private static string GetStructuralOutlineFormConsentLabel(IElement field)
    {
        var label = GetStructuralOutlineAssociatedLabel(field);
        if (LooksLikeStructuralOutlineConsentLabel(label))
        {
            return label;
        }

        foreach (var candidate in GetStructuralOutlineNearbyFieldTexts(field))
        {
            if (LooksLikeStructuralOutlineConsentLabel(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static string GetStructuralOutlineFormActionLabel(IElement element)
    {
        var text = NormalizeWhitespace(element.TextContent);
        if (ShouldKeepStructuralOutlineText(text))
        {
            return text;
        }

        var value = NormalizeWhitespace(element.GetAttribute("value"));
        return ShouldKeepStructuralOutlineText(value) ? value : string.Empty;
    }

    private static string GetStructuralOutlineAssociatedLabel(IElement field)
    {
        var enclosingLabel = field.ParentElement?.Closest("label");
        var enclosingText = NormalizeWhitespace(enclosingLabel?.TextContent);
        if (ShouldKeepStructuralOutlineText(enclosingText))
        {
            return enclosingText;
        }

        var id = NormalizeWhitespace(field.Id);
        if (!string.IsNullOrWhiteSpace(id))
        {
            var form = field.Closest("form");
            var matchingLabel = form?.QuerySelectorAll("label")
                .OfType<IElement>()
                .FirstOrDefault(label => string.Equals(
                    NormalizeWhitespace(label.GetAttribute("for")),
                    id,
                    StringComparison.OrdinalIgnoreCase));
            var matchingText = NormalizeWhitespace(matchingLabel?.TextContent);
            if (ShouldKeepStructuralOutlineText(matchingText))
            {
                return matchingText;
            }
        }

        return string.Empty;
    }

    private static string[] ExtractStructuralOutlineAddressFragments(IElement element)
    {
        var candidates = new List<string>();

        foreach (var addressNode in element.QuerySelectorAll("address").OfType<IElement>())
        {
            var text = NormalizeWhitespace(addressNode.TextContent);
            if (LooksLikeStructuralOutlineAddressFragment(text))
            {
                candidates.Add(text);
            }
        }

        foreach (var textNode in element.QuerySelectorAll("p, li, div, span").OfType<IElement>())
        {
            if (textNode.Closest("select, option, form") is not null)
            {
                continue;
            }

            if (textNode.QuerySelector("p, li, div, span, h1, h2, h3, h4, h5, h6") is not null)
            {
                continue;
            }

            var text = NormalizeWhitespace(textNode.TextContent);
            if (LooksLikeStructuralOutlineAddressFragment(text))
            {
                candidates.Add(text);
            }
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
    }

    private static bool LooksLikeStructuralOutlineAddressFragment(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || CountWords(text) < 3 || CountWords(text) > 20)
        {
            return false;
        }

        if (text.Contains('@', StringComparison.Ordinal) || ExtractStructuralOutlinePhones(text).Length > 0)
        {
            return false;
        }

        if (Regex.IsMatch(text, @"(?:privacy|cookie|terms|copyright|all rights reserved|derechos reservados)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return false;
        }

        return AddressLikeRegex.IsMatch(text);
    }

    private static bool ShouldKeepStructuralOutlineFieldLabel(string? text)
    {
        var normalized = NormalizeWhitespace(text);
        if (!ShouldKeepSemanticText(normalized))
        {
            return false;
        }

        return !Regex.IsMatch(
            normalized,
            @"^(?:search|privacy|privacidad|politique de confidentialite|privacy policy)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeStructuralOutlineConsentLabel(string? text)
    {
        var normalized = NormalizeWhitespace(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return CountWords(normalized) >= 3
            && Regex.IsMatch(
                normalized,
                @"(?:privacy|policy|accept|agree|confirm|read|terms|privacidad|politique|voorwaarden|полит)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static IEnumerable<string> GetStructuralOutlineNearbyFieldTexts(IElement field)
    {
        var candidates = new[]
        {
            field.ParentElement?.TextContent,
            field.ParentElement?.ParentElement?.TextContent,
            field.Closest("label")?.TextContent,
            field.Closest("fieldset")?.TextContent
        };

        return candidates
            .Select(NormalizeWhitespace)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string[] ExtractStructuralOutlinePhones(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : PhoneRegex.Matches(text)
                .Select(match => NormalizeStructuralOutlinePhone(match.Value))
                .Where(value => value.Length >= 8)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();

    private static string[] ExtractStructuralOutlineEmails(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : EmailRegex.Matches(text)
                .Select(match => match.Value.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();

    private static string NormalizeStructuralOutlinePhone(string value)
    {
        var cleaned = Regex.Replace(value, @"[^\d+]", string.Empty, RegexOptions.CultureInvariant);
        return cleaned.StartsWith("00", StringComparison.Ordinal) ? $"+{cleaned[2..]}" : cleaned;
    }

    private static string RenderSemanticTable(IElement table)
    {
        var rows = table.QuerySelectorAll("tr")
            .OfType<IElement>()
            .Select(row => row.Children
                .OfType<IElement>()
                .Where(cell => cell.TagName is "TD" or "TH")
                .Select(cell => NormalizeWhitespace(cell.TextContent))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToArray())
            .Where(cells => cells.Length > 0)
            .ToArray();

        if (rows.Length == 0)
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            rows.Select(cells => "| " + string.Join(" | ", cells) + " |"));
    }

    private static bool ShouldKeepStructuralOutlineText(string? text) =>
        ShouldKeepSemanticText(text);

    private static bool ShouldKeepSemanticImageAlt(IElement image, string? alt)
    {
        var normalizedAlt = NormalizeWhitespace(alt);
        if (!ShouldKeepSemanticText(normalizedAlt))
        {
            return false;
        }

        if (LooksLikeDecorativeImageAlt(normalizedAlt, image))
        {
            return false;
        }

        if (IsLikelyShortCtaText(normalizedAlt))
        {
            return false;
        }

        var adjacentTexts = new List<string>();

        var parentText = NormalizeWhitespace(image.ParentElement?.TextContent);
        if (!string.IsNullOrWhiteSpace(parentText))
        {
            adjacentTexts.Add(parentText);
        }

        var siblingText = image.ParentElement?.Children
            .OfType<IElement>()
            .Where(sibling => !ReferenceEquals(sibling, image))
            .Select(sibling => NormalizeWhitespace(sibling.TextContent))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray() ?? [];

        adjacentTexts.AddRange(siblingText);

        var grandParentText = NormalizeWhitespace(image.ParentElement?.ParentElement?.TextContent);
        if (!string.IsNullOrWhiteSpace(grandParentText))
        {
            adjacentTexts.Add(grandParentText);
        }

        return adjacentTexts
            .Select(NormalizeWhitespace)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .All(adjacent => !adjacent.Contains(normalizedAlt, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeDecorativeImageAlt(string alt, IElement image)
    {
        var normalized = alt.ToLowerInvariant();
        if (Regex.IsMatch(
                normalized,
                @"^(?:linkedin|facebook|instagram|twitter|youtube|logo|icon)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        var ancestorLink = image.Closest("a");
        var href = NormalizeWhitespace(ancestorLink?.GetAttribute("href")).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(href)
            && Regex.IsMatch(href, @"(?:linkedin|facebook|instagram|twitter|youtube)", RegexOptions.CultureInvariant))
        {
            return true;
        }

        return false;
    }

    private static bool IsLikelyShortCtaText(string? text)
    {
        var normalized = NormalizeWhitespace(text).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (CountWords(normalized) > 6)
        {
            return false;
        }

        return Regex.IsMatch(
            normalized,
            @"^(?:view all|view more|read more|click here|learn more|contact us|fale conosco|conhe[çc]a nossos servi[çc]os|saiba mais|top|next|previous|subscribe|book demo|get started|submit)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static int EstimateTokenCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return (int)Math.Ceiling(text.Length / 4.0);
    }

    // === SCORING ===

    private static ScoringResult ComputeContentQualityScore(
        JsonElement llmJson,
        CaptureResult capture,
        MetadataResult metadata)
    {
        var intentType = ReadJsonString(llmJson, "intent", "type") ?? "unknown";
        var issues = ReadIssues(llmJson);
        var tier = ClassifyIntentTier(intentType);
        var applied = new List<PenaltyDetail>();

        var cp = ComputeCompletenessPenalty(tier, intentType, issues, capture, applied);
        var sp = ComputeStructurePenalty(tier, issues, capture, metadata, applied);
        var fp = ComputeFreshnessPenalty(tier, intentType, issues, metadata, llmJson, applied);
        var tp = ComputeTrustRiskPenalty(tier, issues, llmJson, applied);

        return new ScoringResult
        {
            ContentQualityScore = Math.Clamp(100 - cp - sp - fp - tp, 0, 100),
            CompletenessPenalty = cp,
            StructurePenalty = sp,
            FreshnessPenalty = fp,
            TrustRiskPenalty = tp,
            IntentTier = tier.ToString().ToLowerInvariant(),
            AppliedPenalties = applied
        };
    }

    private static IntentTier ClassifyIntentTier(string intent) => intent.ToLowerInvariant() switch
    {
        "pricing" or "contact" or "legal" => IntentTier.S1,
        "referral_stub" or "hub_index" or "profile_bio" => IntentTier.S3,
        _ => IntentTier.S2
    };

    private static int ComputeCompletenessPenalty(
        IntentTier tier, string intentType, IReadOnlyList<string> issues, CaptureResult capture, List<PenaltyDetail> applied)
    {
        // Contact pages are structurally complete when they have the right fields (phone, email,
        // address, form) regardless of word count — skip the word count signal for them.
        var wcPenalty = intentType == "contact" ? 0 : (tier, capture.BodyWordCount) switch
        {
            (IntentTier.S1, < 150) => 22,
            (IntentTier.S1, < 250) => 12,
            (IntentTier.S1, < 400) => 5,
            (IntentTier.S2, < 150) => 14,
            (IntentTier.S2, < 250) => 8,
            (IntentTier.S2, < 400) => 3,
            _ => 0
        };

        // Classification signal — editorial judgment about missing substance
        var classPenalty = 0;
        if (issues.Contains("insufficient_information", StringComparer.Ordinal))
            classPenalty = Math.Max(classPenalty, tier switch { IntentTier.S1 => 18, IntentTier.S2 => 12, _ => 8 });
        if (issues.Contains("missing_expected_next_step", StringComparer.Ordinal))
            classPenalty = Math.Max(classPenalty, tier switch { IntentTier.S1 => 12, IntentTier.S2 => 10, _ => 0 });
        if (issues.Contains("sensitive_topic_without_context", StringComparer.Ordinal))
            classPenalty = Math.Max(classPenalty, tier switch { IntentTier.S1 => 8, IntentTier.S2 => 6, _ => 3 });

        // Max prevents double-counting the same underlying completeness problem
        if (classPenalty >= wcPenalty && classPenalty > 0)
            applied.Add(new PenaltyDetail("completeness.issues", classPenalty));
        else if (wcPenalty > 0)
            applied.Add(new PenaltyDetail("completeness.word_count_low", wcPenalty));

        return Math.Min(Math.Max(wcPenalty, classPenalty), 30);
    }

    private static int ComputeStructurePenalty(
        IntentTier tier, IReadOnlyList<string> issues, CaptureResult capture, MetadataResult metadata,
        List<PenaltyDetail> applied)
    {
        // Hard signals — additive because they are objective, measurable facts
        var titlePenalty = IsGenericOrMissingTitle(metadata.Title) ? tier switch
        {
            IntentTier.S1 => 10, IntentTier.S2 => 8, _ => 4
        } : 0;

        var h1Penalty = capture.H1Headings.Length == 0 ? tier switch
        {
            IntentTier.S1 => 8, IntentTier.S2 => 6, _ => 3
        } : 0;

        var totalHeadings = capture.H1Headings.Length + capture.H2Headings.Length + capture.H3Headings.Length;
        var noHeadingsPenalty = totalHeadings == 0 && capture.BodyWordCount > 400 ? tier switch
        {
            IntentTier.S1 => 6, IntentTier.S2 => 5, _ => 2
        } : 0;

        if (titlePenalty > 0) applied.Add(new PenaltyDetail("structure.missing_or_generic_title", titlePenalty));
        if (h1Penalty > 0) applied.Add(new PenaltyDetail("structure.missing_h1", h1Penalty));
        if (noHeadingsPenalty > 0) applied.Add(new PenaltyDetail("structure.no_headings_long_page", noHeadingsPenalty));

        // Classification signal — editorial judgment about messaging or structural fit
        var classPenalty = 0;
        if (issues.Contains("language_mismatch", StringComparer.Ordinal))
            classPenalty = Math.Max(classPenalty, tier switch { IntentTier.S1 => 18, IntentTier.S2 => 15, _ => 8 });
        if (issues.Contains("unclear_messaging", StringComparer.Ordinal))
            classPenalty = Math.Max(classPenalty, tier switch { IntentTier.S1 => 12, IntentTier.S2 => 10, _ => 5 });
        if (issues.Contains("title_body_mismatch", StringComparer.Ordinal))
            classPenalty = Math.Max(classPenalty, tier switch { IntentTier.S1 => 10, IntentTier.S2 => 8, _ => 4 });
        if (issues.Contains("url_content_mismatch", StringComparer.Ordinal))
            classPenalty = Math.Max(classPenalty, tier switch { IntentTier.S1 => 10, IntentTier.S2 => 8, _ => 4 });

        if (classPenalty > 0)
            applied.Add(new PenaltyDetail("structure.issues", classPenalty));

        return Math.Min(titlePenalty + h1Penalty + noHeadingsPenalty + classPenalty, 25);
    }

    private static int ComputeFreshnessPenalty(
        IntentTier tier, string intentType, IReadOnlyList<string> issues, MetadataResult metadata, JsonElement diagnosis,
        List<PenaltyDetail> applied)
    {
        var orientation = ReadTemporalAssessmentString(diagnosis, "orientation") ?? "unclear";
        var referenceStatus = ReadTemporalAssessmentString(diagnosis, "referenceStatus") ?? "unclear";
        var appearsCurrentToReader = ReadTemporalAssessmentBool(diagnosis, "appearsCurrentToReader") ?? false;
        var historicalContextClear = ReadTemporalAssessmentBool(diagnosis, "historicalContextClear") ?? false;

        var newestYear = GetNewestContentYear(metadata);
        var currentYear = DateTime.UtcNow.Year;

        var agePenalty = 0;
        var ageLabel = string.Empty;
        if (newestYear.HasValue && ShouldApplyAgePenalty(intentType, orientation, referenceStatus, appearsCurrentToReader, historicalContextClear))
        {
            var age = currentYear - newestYear.Value;
            agePenalty = (tier, age) switch
            {
                (IntentTier.S1, >= 5) => 14,
                (IntentTier.S1, >= 3) => 10,
                (IntentTier.S2, >= 5) => 10,
                (IntentTier.S2, >= 3) => 7,
                (IntentTier.S3, >= 5) => 3,
                (IntentTier.S3, >= 3) => 2,
                _ => 0
            };
            if (agePenalty > 0) ageLabel = $"freshness.content_age_{age}y";
        }

        // Externally verified and page-internal temporal issues carry lower risk on archival articles.
        var classPenalty = 0;
        var hasCurrentClaimIssue = issues.Contains("verified_current_claim_issue", StringComparer.Ordinal);
        var hasOutdatedClaimIssue = hasCurrentClaimIssue && HasFactVerdict(diagnosis, "outdated");
        if (hasOutdatedClaimIssue
            || issues.Contains("expired_date_or_deadline", StringComparer.Ordinal)
            || issues.Contains("misleading_current_status", StringComparer.Ordinal))
        {
            classPenalty = (tier, intentType) switch
            {
                (IntentTier.S1, _) => 18,
                (_, "article") => 6,
                (IntentTier.S2, _) => 12,
                _ => 4
            };
        }
        else if (orientation == "preview" && referenceStatus == "past" && appearsCurrentToReader)
        {
            classPenalty = tier switch
            {
                IntentTier.S1 => 14,
                IntentTier.S2 => 10,
                _ => 4
            };
            applied.Add(new PenaltyDetail("freshness.stale_preview_context", classPenalty));
            return Math.Min(Math.Max(agePenalty, classPenalty), 25);
        }

        // Max — both signals describe the same freshness problem
        if (classPenalty >= agePenalty && classPenalty > 0)
            applied.Add(new PenaltyDetail("freshness.issues", classPenalty));
        else if (agePenalty > 0)
            applied.Add(new PenaltyDetail(ageLabel, agePenalty));

        return Math.Min(Math.Max(agePenalty, classPenalty), 25);
    }

    private static bool ShouldApplyAgePenalty(
        string intentType,
        string orientation,
        string referenceStatus,
        bool appearsCurrentToReader,
        bool historicalContextClear)
    {
        if (orientation == "historical_record" && historicalContextClear)
        {
            return false;
        }

        if (orientation == "evergreen")
        {
            return false;
        }

        if (orientation == "preview")
        {
            return referenceStatus is "past" or "mixed" || appearsCurrentToReader;
        }

        if (orientation == "current_state")
        {
            return true;
        }

        return appearsCurrentToReader || intentType is "pricing" or "legal" or "support_doc";
    }

    private static int ComputeTrustRiskPenalty(
        IntentTier tier, IReadOnlyList<string> issues, JsonElement diagnosis, List<PenaltyDetail> applied)
    {
        var penalty = issues.Contains("verified_current_claim_issue", StringComparer.Ordinal)
            ? tier switch
            {
                IntentTier.S1 when HasFactVerdict(diagnosis, "false") => 18,
                IntentTier.S2 when HasFactVerdict(diagnosis, "false") => 14,
                IntentTier.S3 when HasFactVerdict(diagnosis, "false") => 8,
                _ => 0
            }
            : issues.Contains("verified_historical_claim_issue", StringComparer.Ordinal)
                ? tier switch
                {
                    IntentTier.S1 => 8,
                    IntentTier.S2 => 6,
                    _ => 3
                }
                : 0;

        if (issues.Contains("brand_safety_risk", StringComparer.Ordinal))
            penalty = Math.Max(penalty, tier switch { IntentTier.S1 => 18, IntentTier.S2 => 16, _ => 12 });
        if (issues.Contains("within_page_inconsistency", StringComparer.Ordinal))
            penalty = Math.Max(penalty, tier switch { IntentTier.S1 => 16, IntentTier.S2 => 14, _ => 8 });
        if (issues.Contains("missing_trust_context", StringComparer.Ordinal))
            penalty = Math.Max(penalty, tier switch { IntentTier.S1 => 10, IntentTier.S2 => 8, _ => 4 });

        if (penalty > 0)
            applied.Add(new PenaltyDetail("trust.issues", penalty));

        return Math.Min(penalty, 20);
    }

    private static bool IsGenericOrMissingTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return true;
        var trimmed = title.Trim();
        if (trimmed.Length < 15) return true;
        var pageTitle = trimmed.Split('|')[0].Trim().ToLowerInvariant();
        return pageTitle is "home" or "page" or "services" or "welcome" or "index" or "about" or "contact";
    }

    private static int? GetNewestContentYear(MetadataResult metadata)
    {
        var years = new List<int>();
        TryExtractYear(metadata.PublishedDate, years);
        TryExtractYear(metadata.ModifiedDate, years);
        return years.Count > 0 ? years.Max() : null;
    }

    private static void TryExtractYear(string? dateStr, List<int> years)
    {
        if (string.IsNullOrWhiteSpace(dateStr)) return;
        var match = Regex.Match(dateStr, @"\b(20\d{2}|19\d{2})\b");
        if (match.Success && int.TryParse(match.Value, out var year))
            years.Add(year);
    }

    private static string NormalizeClaimDate(string? value)
    {
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", out var parsed)
            ? parsed.ToString("yyyy-MM-dd")
            : DateTime.UtcNow.ToString("yyyy-MM-dd");
    }

    private static JsonArray BuildContextArray(JsonElement request)
    {
        var values = ReadJsonArrayValues(request, "context");
        var context = new JsonArray();
        foreach (var value in values)
        {
            context.Add(value);
        }

        return context;
    }

    private static string ResolveValidatedFactCheckVerdict(JsonElement requestedClaim, JsonElement returnedCheck)
    {
        var rawVerdict = ReadJsonString(returnedCheck, "verdict") ?? "unverifiable";
        var historicalValidity = NormalizeHistoricalValidity(ReadJsonString(returnedCheck, "historicalValidity"), rawVerdict);
        var currentValidity = NormalizeCurrentValidity(ReadJsonString(returnedCheck, "currentValidity"), rawVerdict);
        var presentedAsCurrent = ReadJsonBool(requestedClaim, "presentedAsCurrent") == true;

        if (historicalValidity == "supported" && currentValidity == "outdated")
        {
            return presentedAsCurrent
                || ReadJsonString(requestedClaim, "currentImpact") == "update_required"
                ? "outdated"
                : "supported";
        }

        return rawVerdict is "supported" or "contradicted" or "outdated" or "partially_supported" or "unverifiable"
            ? rawVerdict
            : "unverifiable";
    }

    private static string NormalizeHistoricalValidity(string? value, string verdict)
    {
        if (value is "supported" or "contradicted" or "unknown")
        {
            return value;
        }

        return verdict switch
        {
            "supported" or "outdated" or "partially_supported" => "supported",
            "contradicted" => "contradicted",
            _ => "unknown"
        };
    }

    private static string NormalizeCurrentValidity(string? value, string verdict)
    {
        if (value is "supported" or "outdated" or "contradicted" or "unknown")
        {
            return value;
        }

        return verdict switch
        {
            "supported" => "supported",
            "outdated" => "outdated",
            "contradicted" => "contradicted",
            _ => "unknown"
        };
    }

    private static JsonElement? ReadJsonObject(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var key in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out var next))
                return null;
            current = next;
        }

        return current.ValueKind == JsonValueKind.Object ? current : null;
    }

    private static string? ReadJsonString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var key in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out var next))
                return null;
            current = next;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string ReadJsonArrayStrings(JsonElement? element, string propertyName)
    {
        if (element is null
            || element.Value.ValueKind != JsonValueKind.Object
            || !element.Value.TryGetProperty(propertyName, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return "<none>";
        }

        var values = array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();

        return values.Length == 0 ? "<none>" : string.Join("; ", values);
    }

    private static List<string> ReadJsonArrayValues(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToList();
    }

    private static double? ReadJsonDouble(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var key in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out var next))
                return null;
            current = next;
        }

        return current.ValueKind == JsonValueKind.Number && current.TryGetDouble(out var value)
            ? value
            : null;
    }

    private static bool? ReadJsonBool(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var key in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out var next))
                return null;
            current = next;
        }

        return current.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string? ReadTemporalAssessmentString(JsonElement diagnosis, string propertyName) =>
        ReadJsonString(diagnosis, "temporalAssessment", propertyName);

    private static bool? ReadTemporalAssessmentBool(JsonElement diagnosis, string propertyName) =>
        ReadJsonBool(diagnosis, "temporalAssessment", propertyName);

}

internal sealed class PipelineResult
{
    public string Url { get; init; } = string.Empty;
    public string FinalUrl { get; init; } = string.Empty;
    public string OutputLanguage { get; init; } = "en";
    public DateTimeOffset ExecutedAt { get; init; }
    public CaptureResult Capture { get; init; } = new();
    public MetadataResult Metadata { get; init; } = new();
    public StructuralOutlineResult StructuralOutline { get; init; } = new();
    public ExtractionResult Extraction { get; init; } = new();
    public JsonElement? Analysis { get; init; }
    public LlmResultMetadata? Llm { get; init; }
    public ScoringResult? Scoring { get; init; }
}

internal sealed class CaptureResult
{
    public int? StatusCode { get; init; }
    public string? FinalUrl { get; init; }
    public string? ContentType { get; init; }
    public string? UnsupportedContentType { get; init; }
    public string? BlockedReason { get; init; }
    [JsonIgnore]
    public string Html { get; init; } = string.Empty;
    [JsonIgnore]
    public string BodyText { get; init; } = string.Empty;
    public int BodyWordCount { get; init; }
    public string[] H1Headings { get; init; } = [];
    public string[] H2Headings { get; init; } = [];
    public string[] H3Headings { get; init; } = [];
}

internal sealed class MetadataResult
{
    public string Title { get; init; } = string.Empty;
    public string MetaDescription { get; init; } = string.Empty;
    public string CanonicalUrl { get; init; } = string.Empty;
    public string SiteName { get; init; } = string.Empty;
    public string Language { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string PublishedDate { get; init; } = string.Empty;
    public string ModifiedDate { get; init; } = string.Empty;
    public string UrlPatternHint { get; init; } = "/";
    public bool PaywallDetected { get; init; }
}

internal sealed class ExtractionResult
{
    public string Method { get; init; } = "structural-outline";
    public int EstimatedTokens { get; init; }
    public bool TruncatedByTextLimit { get; init; }
}

internal sealed class StructuralOutlineResult
{
    public StructuralOutlineSummary Summary { get; init; } = new();
    public string Markdown { get; init; } = string.Empty;
}

internal sealed class StructuralOutlineSummary
{
    public int EstimatedTokens { get; init; }
    public bool TruncatedByTextLimit { get; init; }
}

internal sealed class LlmOutput
{
    public string Provider { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    [JsonIgnore]
    public string RawResponse { get; init; } = string.Empty;
    [JsonIgnore]
    public string ParsedResponse { get; init; } = string.Empty;
    [JsonIgnore]
    public JsonElement? ParsedJson { get; init; }
    public string? Error { get; init; }
    public string[] WebSourceUrls { get; init; } = [];
    [JsonIgnore]
    public IReadOnlyDictionary<string, string>? RetrievedSourceContents { get; init; }
}

internal sealed record ExternalEvidencePacket(
    string PromptText,
    string[] SourceUrls,
    IReadOnlyDictionary<string, string> SourceContents);

internal sealed record EvidenceValidationResult(JsonArray Evidence, string Status);

internal sealed record ExternalEvidenceSource(
    string ClaimId,
    string Url,
    string Title,
    string Content,
    string RetrievedContent);

internal sealed record TavilySearchPlan(string Query, string SearchDepth, int MaxResults);

internal sealed class LlmResultMetadata
{
    public string Provider { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string? Error { get; init; }
    public string[] WebSourceUrls { get; init; } = [];
}

internal sealed class PageRegion
{
    public string Name { get; init; } = string.Empty;
    public IElement Element { get; init; } = default!;
}

internal enum LlmStage
{
    Intent,
    TemporalGrounding,
    Diagnosis,
    VerificationSelection,
    FactCheck,
    EvidenceEvaluation,
    Recommendation
}

internal enum IntentTier { S1, S2, S3 }

internal sealed class ScoringResult
{
    public int ContentQualityScore { get; init; }
    public int CompletenessPenalty { get; init; }
    public int StructurePenalty { get; init; }
    public int FreshnessPenalty { get; init; }
    public int TrustRiskPenalty { get; init; }
    public string IntentTier { get; init; } = "s2";
    public IReadOnlyList<PenaltyDetail> AppliedPenalties { get; init; } = [];
}

internal sealed class PenaltyDetail
{
    public string Signal { get; init; } = string.Empty;
    public int Points { get; init; }

    public PenaltyDetail(string signal, int points)
    {
        Signal = signal;
        Points = points;
    }
}
