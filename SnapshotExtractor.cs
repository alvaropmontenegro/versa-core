using AngleSharp;
using AngleSharp.Dom;
using Microsoft.Playwright;
using System.Text;
using System.Text.RegularExpressions;

namespace VersaCore;

// Immutable page snapshot used by every pipeline. It has no LLM, retrieval, or verdict logic.
internal static class SnapshotExtractor
{
    public static async Task<CaptureResult> CaptureAsync(string url)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync(new() { UserAgent = AppConstants.BrowserUserAgent });
        var response = await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = AppConstants.NavigationTimeoutMs });
        await page.WaitForTimeoutAsync(AppConstants.PostDomContentLoadedDelayMs);
        for (var i = 0; i < AppConstants.ScrollPasses; i++) { await page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)"); await page.WaitForTimeoutAsync(AppConstants.ScrollDelayMs); }
        await page.EvaluateAsync(LayoutAnnotationScript);
        var html = await page.ContentAsync();
        var body = await page.Locator("body").InnerTextAsync();
        return new CaptureResult { StatusCode = response?.Status, FinalUrl = page.Url, Html = html, BodyText = body, BodyWordCount = CountWords(body) };
    }

    public static async Task<IDocument> ParseDocumentAsync(string html) =>
        await BrowsingContext.New(Configuration.Default).OpenAsync(request => request.Content(html));

    public static bool IsAccessInterstitial(CaptureResult capture)
    {
        var text = capture.BodyText.ToLowerInvariant();
        return text.Contains("checking your browser") || text.Contains("javascript required") ||
               text.Contains("verify you are human") || text.Contains("captcha") || text.Contains("just a moment");
    }

    public static Task<MetadataResult> ExtractMetadata(IDocument document, string finalUrl) => Task.FromResult(new MetadataResult
    {
        Title = document.Title ?? string.Empty,
        MetaDescription = Meta(document, "description"),
        CanonicalUrl = document.QuerySelector("link[rel='canonical']")?.GetAttribute("href") ?? finalUrl,
        Language = document.DocumentElement?.GetAttribute("lang") ?? "unknown",
        PublishedDate = Meta(document, "article:published_time"),
        ModifiedDate = Meta(document, "article:modified_time")
    });

    public static async Task<StructuralOutlineResult> BuildStructuralOutlineAsync(IDocument document)
    {
        // Old frozen captures have no layout metadata. Resolve their embedded CSS
        // offline, with page scripts and network disabled, before extracting text.
        if (document.DocumentElement?.GetAttribute("data-versa-layout-version") != "1")
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync(new() { JavaScriptEnabled = false });
            await page.RouteAsync("**/*", route => route.AbortAsync());
            await page.SetContentAsync(document.DocumentElement?.OuterHtml ?? "", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            var displays = await page.EvaluateAsync<string[]>("() => Array.from(document.querySelectorAll('*'), e => getComputedStyle(e).display)");
            var elements = document.QuerySelectorAll("*");
            if (elements.Length != displays.Length) throw new InvalidDataException("Layout reconstruction changed the DOM structure.");
            for (var i = 0; i < elements.Length; i++)
                elements[i].SetAttribute("data-versa-display", displays[i]);
        }
        var blocks = new List<string>();
        if (document.Body is { } body) AppendVisibleBlocks(body, blocks);
        var markdown = string.Join("\n\n", blocks);
        return new StructuralOutlineResult { Markdown = markdown, Summary = new StructuralOutlineSummary { EstimatedTokens = markdown.Length / 4, TruncatedByTextLimit = false } };
    }

    private const string LayoutAnnotationScript = """
        () => {
            for (const e of document.querySelectorAll('*')) e.setAttribute('data-versa-display', getComputedStyle(e).display);
            document.documentElement.setAttribute('data-versa-layout-version', '1');
        }
        """;

    public static string CreateOutputDirectory(Uri uri) => Path.Combine(AppConstants.OutputRoot, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Regex.Replace(uri.Host + uri.AbsolutePath, "[^a-zA-Z0-9]+", "-").Trim('-').ToLowerInvariant()}");
    private static string? Meta(IDocument document, string name) => document.QuerySelector($"meta[name='{name}'],meta[property='{name}']")?.GetAttribute("content");
    private static void AppendVisibleBlocks(IElement element, List<string> blocks)
    {
        if (IsNoise(element)) return;

        var tag = element.LocalName.ToLowerInvariant();
        if (tag.Length == 2 && tag[0] == 'h' && char.IsDigit(tag[1]))
        {
            Add(blocks, $"<heading level=\"{tag[1]}\">{InlineText(element)}</heading>");
            return;
        }
        if (tag is "p" or "blockquote" or "address" or "pre")
        {
            Add(blocks, $"<prose>{InlineText(element)}</prose>");
            return;
        }
        if (tag == "li")
        {
            Add(blocks, $"<list_item>{InlineText(element)}</list_item>");
            foreach (var child in element.Children.Where(child => child.LocalName is "ul" or "ol")) AppendVisibleBlocks(child, blocks);
            return;
        }
        if (tag == "a" && IsInlineAnchor(element)) return;
        if (tag is "a" or "button")
        {
            Add(blocks, $"<cta>{WithAccessibleContext(element, InlineText(element))}</cta>");
            return;
        }
        if (tag == "form")
        {
            Add(blocks, "<form>");
            foreach (var child in element.Children) AppendVisibleBlocks(child, blocks);
            Add(blocks, "</form>");
            return;
        }
        if (tag == "table")
        {
            Add(blocks, $"<table>{InlineText(element)}</table>");
            return;
        }

        var pending = new StringBuilder();
        AppendReadingNodes(element.ChildNodes, blocks, pending);
        Add(blocks, WrapUiText(Clean(pending.ToString())));
    }

    private static void AppendReadingNodes(IEnumerable<INode> nodes, List<string> blocks, StringBuilder pending)
    {
        foreach (var node in nodes)
        {
            if (node is IText text) { pending.Append(text.Data); continue; }
            if (node is not IElement child || IsNoise(child)) continue;
            if (child.LocalName == "br") { pending.Append(' '); continue; }
            if (IsBlockElement(child))
            {
                Add(blocks, WrapUiText(Clean(pending.ToString())));
                pending.Clear();
                AppendVisibleBlocks(child, blocks);
            }
            else AppendReadingNodes(child.ChildNodes, blocks, pending);
        }
    }

    private static string InlineText(IElement element)
    {
        var builder = new StringBuilder();
        AppendInlineText(element, builder, skipBlockChildren: false);
        return Clean(builder.ToString());
    }

    private static string WithAccessibleContext(IElement element, string visibleText)
    {
        var accessibleLabel = Clean(element.GetAttribute("aria-label") ?? string.Empty);
        if (string.IsNullOrWhiteSpace(accessibleLabel))
        {
            accessibleLabel = Clean(string.Join(' ', element.QuerySelectorAll("img[alt]").Select(image => image.GetAttribute("alt"))));
        }
        if (string.IsNullOrWhiteSpace(accessibleLabel) || string.Equals(accessibleLabel, visibleText, StringComparison.OrdinalIgnoreCase)) return visibleText;
        return $"{accessibleLabel} — {visibleText}";
    }

    private static void AppendInlineText(INode node, StringBuilder builder, bool skipBlockChildren)
    {
        if (node is IText text) { builder.Append(text.Data); return; }
        if (node is not IElement element || IsNoise(element)) return;
        if (element.LocalName == "br") { builder.Append(' '); return; }
        var isBlock = IsBlockElement(element);
        if (isBlock) builder.Append(' ');
        if (skipBlockChildren && isBlock) return;
        foreach (var child in element.ChildNodes) AppendInlineText(child, builder, skipBlockChildren);
        if (isBlock) builder.Append(' ');
    }

    private static bool IsBlockElement(IElement element)
    {
        if (element.GetAttribute("data-versa-display") is "block" or "flex" or "grid" or "table" or "list-item" or "flow-root") return true;
        if (element.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase)) return !IsInlineAnchor(element);
        return element.LocalName.ToLowerInvariant() is
        "address" or "article" or "aside" or "blockquote" or "button" or "div" or "dl" or "fieldset" or "figure" or "figcaption" or "form" or
        "footer" or "header" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "li" or "main" or "nav" or "ol" or "p" or
        "pre" or "section" or "table" or "ul";
    }

    private static bool IsInlineAnchor(IElement element)
    {
        if (!element.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase)) return false;
        return HasAdjacentInlineText(element.PreviousSibling) || HasAdjacentInlineText(element.NextSibling);
    }

    private static bool HasAdjacentInlineText(INode? node)
    {
        if (node is IText text) return !string.IsNullOrWhiteSpace(text.Data);
        if (node is not IElement element || IsNoise(element)) return false;
        // Do not classify an adjacent anchor through IsBlockElement: that would
        // ask whether this anchor is inline, then inspect the original anchor
        // again and recurse forever. Adjacent links are not surrounding prose.
        if (element.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase) || IsBlockElement(element)) return false;
        return !string.IsNullOrWhiteSpace(element.TextContent);
    }

    private static bool IsNoise(IElement element)
    {
        if (element.GetAttribute("data-versa-display") == "none") return true;
        var tag = element.LocalName.ToLowerInvariant();
        if (tag is "script" or "style" or "noscript" or "svg" or "template" or "iframe" or "canvas" or "head" or "nav" or "footer") return true;
        if (element.HasAttribute("hidden") || string.Equals(element.GetAttribute("aria-hidden"), "true", StringComparison.OrdinalIgnoreCase)) return true;
        var role = element.GetAttribute("role")?.ToLowerInvariant();
        if (role is "navigation" or "banner" or "contentinfo" or "dialog" or "alertdialog" or "menu" or "menubar") return true;
        var markers = string.Join(' ', element.GetAttribute("id"), element.GetAttribute("class"), element.GetAttribute("data-testid"), element.GetAttribute("aria-label")).ToLowerInvariant();
        return markers.Contains("cookie") || markers.Contains("consent") || markers.Contains("mobile-menu") || markers.Contains("mobile_menu") ||
               markers.Contains("hamburger") || markers.Contains("chat-widget") || markers.Contains("recaptcha");
    }

    private static void Add(List<string> blocks, string text)
    {
        if (!string.IsNullOrWhiteSpace(text)) blocks.Add(text);
    }
    private static string WrapUiText(string text) => string.IsNullOrWhiteSpace(text) ? string.Empty : $"<ui_text>{text}</ui_text>";
    private static string Clean(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static int CountWords(string text) => string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

internal sealed class PipelineResult { public string Url { get; init; } = ""; public string FinalUrl { get; init; } = ""; public string OutputLanguage { get; init; } = "en"; public DateTimeOffset ExecutedAt { get; init; } public CaptureResult Capture { get; init; } = new(); public MetadataResult Metadata { get; init; } = new(); public StructuralOutlineResult StructuralOutline { get; init; } = new(); public System.Text.Json.JsonElement? Analysis { get; init; } public LlmResultMetadata? Llm { get; init; } }
internal sealed class CaptureResult { public int? StatusCode { get; init; } public string? FinalUrl { get; init; } public string Html { get; init; } = ""; public string BodyText { get; init; } = ""; public int BodyWordCount { get; init; } }
internal sealed class MetadataResult { public string Title { get; init; } = ""; public string? MetaDescription { get; init; } public string CanonicalUrl { get; init; } = ""; public string Language { get; init; } = "unknown"; public string? PublishedDate { get; init; } public string? ModifiedDate { get; init; } }
internal sealed class StructuralOutlineResult { public string Markdown { get; init; } = ""; public StructuralOutlineSummary Summary { get; init; } = new(); }
internal sealed class StructuralOutlineSummary { public int EstimatedTokens { get; init; } public bool TruncatedByTextLimit { get; init; } }
internal sealed class LlmResultMetadata { public string Provider { get; init; } = ""; public string Model { get; init; } = ""; public string? Error { get; init; } public string[] WebSourceUrls { get; init; } = []; }
