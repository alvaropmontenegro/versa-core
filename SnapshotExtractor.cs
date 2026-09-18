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
        await using var context = await browser.NewContextAsync(new() { UserAgent = AppConstants.BrowserUserAgent });
        return await CaptureAsync(url, context);
    }

    public static async Task<CaptureResult> CaptureAsync(string url, IBrowserContext context)
    {
        var page = await context.NewPageAsync();
        try
        {
            int? statusCode = null;
            page.Response += (_, response) =>
            {
                if (response.Request.IsNavigationRequest && response.Frame == page.MainFrame)
                    statusCode = response.Status;
            };
            await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = AppConstants.NavigationTimeoutMs });
            await page.WaitForTimeoutAsync(AppConstants.PostDomContentLoadedDelayMs);
            try
            {
                await page.WaitForFunctionAsync("""
                    () => {
                        if (!document.body) return false;
                        const body = document.body.innerText.trim().toLowerCase();
                        const title = document.title.toLowerCase();
                        const markers = ['checking your browser', 'verify you are human',
                            'confirm you are human', 'just a moment', 'javascript required',
                            'this will only take a few seconds'];
                        return !markers.some(marker => title.includes(marker) ||
                            (body.length < 2000 && body.includes(marker)));
                    }
                    """, null, new() { Timeout = 30000 });
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException($"capture_blocked: Browser verification did not finish within 30 seconds for {url}.");
            }
            for (var i = 0; i < AppConstants.ScrollPasses; i++) { await page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)"); await page.WaitForTimeoutAsync(AppConstants.ScrollDelayMs); }
            await page.EvaluateAsync(LayoutAnnotationScript);
            var html = await page.ContentAsync();
            var body = await page.Locator("body").InnerTextAsync();
            return new CaptureResult { StatusCode = statusCode, FinalUrl = page.Url, Html = html, BodyText = body, BodyWordCount = CountWords(body) };
        }
        finally
        {
            await page.CloseAsync();
        }
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
        var builder = new StructuralOutlineBuilder();
        if (document.Body is { } body) AppendVisibleBlocks(body, builder);
        var markdown = string.Join("\n\n", builder.RenderedBlocks);
        return new StructuralOutlineResult
        {
            Blocks = builder.Blocks,
            Markdown = markdown,
            Summary = new StructuralOutlineSummary { EstimatedTokens = markdown.Length / 4, TruncatedByTextLimit = false }
        };
    }

    private const string LayoutAnnotationScript = """
        () => {
            for (const e of document.querySelectorAll('*')) e.setAttribute('data-versa-display', getComputedStyle(e).display);
            document.documentElement.setAttribute('data-versa-layout-version', '1');
        }
        """;

    public static string CreateOutputDirectory(Uri uri) => Path.Combine(AppConstants.OutputRoot, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Regex.Replace(uri.Host + uri.AbsolutePath, "[^a-zA-Z0-9]+", "-").Trim('-').ToLowerInvariant()}");
    private static string? Meta(IDocument document, string name) => document.QuerySelector($"meta[name='{name}'],meta[property='{name}']")?.GetAttribute("content");
    private static void AppendVisibleBlocks(IElement element, StructuralOutlineBuilder builder)
    {
        if (IsNoise(element)) return;

        var tag = element.LocalName.ToLowerInvariant();
        if (tag.Length == 2 && tag[0] == 'h' && char.IsDigit(tag[1]))
        {
            builder.Add(element, ContentBlockKind.Heading, InlineText(element), TextSource.Visible, level: tag[1] - '0');
            return;
        }
        if (tag is "p" or "blockquote" or "address" or "pre")
        {
            builder.Add(element, ContentBlockKind.Prose, InlineText(element), TextSource.Visible);
            AppendAccessibleImages(element, builder);
            return;
        }
        if (tag == "li")
        {
            builder.Add(element, ContentBlockKind.ListItem, InlineText(element), TextSource.Visible);
            AppendAccessibleImages(element, builder);
            foreach (var child in element.Children.Where(child => child.LocalName is "ul" or "ol")) AppendVisibleBlocks(child, builder);
            return;
        }
        if (tag == "a" && IsInlineAnchor(element)) return;
        if (tag is "a" or "button")
        {
            AppendInteractiveElement(element, builder);
            return;
        }
        if (tag == "img")
        {
            AppendAccessibleImage(element, builder);
            return;
        }
        if (tag == "form")
        {
            builder.AddMarker("<form>");
            foreach (var child in element.Children) AppendVisibleBlocks(child, builder);
            builder.AddMarker("</form>");
            return;
        }
        if (tag == "table")
        {
            builder.Add(element, ContentBlockKind.Table, InlineText(element), TextSource.Visible);
            AppendAccessibleImages(element, builder);
            return;
        }

        var pending = new StringBuilder();
        AppendReadingNodes(element.ChildNodes, builder, pending, element);
        builder.Add(element, ContentBlockKind.UiText, Clean(pending.ToString()), TextSource.Visible);
    }

    private static void AppendReadingNodes(IEnumerable<INode> nodes, StructuralOutlineBuilder builder, StringBuilder pending, IElement owner)
    {
        foreach (var node in nodes)
        {
            if (node is IText text) { pending.Append(text.Data); continue; }
            if (node is not IElement child || IsNoise(child)) continue;
            if (child.LocalName == "br") { pending.Append(' '); continue; }
            if (child.LocalName == "img")
            {
                builder.Add(owner, ContentBlockKind.UiText, Clean(pending.ToString()), TextSource.Visible);
                pending.Clear();
                AppendAccessibleImage(child, builder);
                continue;
            }
            if (IsBlockElement(child))
            {
                builder.Add(owner, ContentBlockKind.UiText, Clean(pending.ToString()), TextSource.Visible);
                pending.Clear();
                AppendVisibleBlocks(child, builder);
            }
            else AppendReadingNodes(child.ChildNodes, builder, pending, owner);
        }
    }

    private static string InlineText(IElement element)
    {
        var builder = new StringBuilder();
        AppendInlineText(element, builder, skipBlockChildren: false);
        return Clean(builder.ToString());
    }

    private static void AppendInteractiveElement(IElement element, StructuralOutlineBuilder builder)
    {
        var fragments = new List<TextFragment>();
        var visibleText = InlineText(element);
        if (!string.IsNullOrWhiteSpace(visibleText))
            fragments.Add(new TextFragment(TextSource.Visible, visibleText, element.LocalName, null, BuildLocator(element)));

        var accessible = GetAccessibleText(element);
        if (accessible is not null && !string.Equals(accessible.Text, visibleText, StringComparison.OrdinalIgnoreCase))
            fragments.Add(accessible);

        builder.Add(element, ContentBlockKind.Cta, fragments);
    }

    private static TextFragment? GetAccessibleText(IElement element)
    {
        var ariaLabel = Clean(element.GetAttribute("aria-label") ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(ariaLabel))
            return new TextFragment(TextSource.AriaLabel, ariaLabel, element.LocalName, "aria-label", BuildLocator(element));

        var labelledBy = element.GetAttribute("aria-labelledby")?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
        var labelledByText = Clean(string.Join(' ', labelledBy
            .Select(id => element.Owner?.GetElementById(id)?.TextContent)
            .Where(text => !string.IsNullOrWhiteSpace(text))));
        if (!string.IsNullOrWhiteSpace(labelledByText))
            return new TextFragment(TextSource.AriaLabelledBy, labelledByText, element.LocalName, "aria-labelledby", BuildLocator(element));

        var image = element.QuerySelector("img[alt]");
        var altText = Clean(string.Join(' ', element.QuerySelectorAll("img[alt]").Select(item => item.GetAttribute("alt"))));
        if (!string.IsNullOrWhiteSpace(altText))
            return new TextFragment(TextSource.AltText, altText, image?.LocalName ?? "img", "alt", image is null ? BuildLocator(element) : BuildLocator(image));

        var title = Clean(element.GetAttribute("title") ?? string.Empty);
        return string.IsNullOrWhiteSpace(title)
            ? null
            : new TextFragment(TextSource.Title, title, element.LocalName, "title", BuildLocator(element));
    }

    private static void AppendAccessibleImages(IElement container, StructuralOutlineBuilder builder)
    {
        foreach (var image in container.QuerySelectorAll("img[alt]"))
        {
            if (image.Closest("a, button") is not null) continue;
            AppendAccessibleImage(image, builder);
        }
    }

    private static void AppendAccessibleImage(IElement image, StructuralOutlineBuilder builder)
    {
        if (!image.HasAttribute("alt")) return;
        var altText = Clean(image.GetAttribute("alt") ?? string.Empty);
        if (string.IsNullOrWhiteSpace(altText)) return;
        builder.Add(image, ContentBlockKind.Image,
            [new TextFragment(TextSource.AltText, altText, "img", "alt", BuildLocator(image))]);
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

    internal static string BuildLocator(IElement element)
    {
        var parts = new Stack<string>();
        for (var current = element; current is not null && current.LocalName != "html"; current = current.ParentElement)
        {
            var id = current.GetAttribute("id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                parts.Push($"{current.LocalName}#{id}");
                break;
            }

            var testId = current.GetAttribute("data-testid");
            if (!string.IsNullOrWhiteSpace(testId))
            {
                parts.Push($"{current.LocalName}[data-testid='{testId}']");
                break;
            }

            var sameTagSiblings = current.ParentElement?.Children
                .Where(sibling => sibling.LocalName == current.LocalName).ToArray() ?? [];
            var position = Array.IndexOf(sameTagSiblings, current) + 1;
            parts.Push(sameTagSiblings.Length > 1 ? $"{current.LocalName}:nth-of-type({position})" : current.LocalName);
        }

        return string.Join(" > ", parts);
    }

    private static string Clean(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static int CountWords(string text) => string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

internal sealed class StructuralOutlineBuilder
{
    private readonly List<ContentBlock> blocks = [];
    private readonly List<string> renderedBlocks = [];
    private string? currentSection;

    public IReadOnlyList<ContentBlock> Blocks => blocks;
    public IReadOnlyList<string> RenderedBlocks => renderedBlocks;

    public void Add(IElement element, ContentBlockKind kind, string text, TextSource source, int? level = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Add(element, kind, [new TextFragment(source, text, element.LocalName, null, SnapshotExtractor.BuildLocator(element))], level);
    }

    public void Add(IElement element, ContentBlockKind kind, IReadOnlyList<TextFragment> fragments, int? level = null)
    {
        if (fragments.Count == 0) return;

        var block = new ContentBlock(
            $"block-{blocks.Count + 1:0000}",
            kind,
            new ElementLocation(SnapshotExtractor.BuildLocator(element), currentSection, element.LocalName),
            fragments,
            level);

        blocks.Add(block);
        renderedBlocks.Add(Render(block));

        if (kind == ContentBlockKind.Heading)
            currentSection = fragments.FirstOrDefault(fragment => fragment.Source == TextSource.Visible)?.Text;
    }

    public void AddMarker(string marker) => renderedBlocks.Add(marker);

    private static string Render(ContentBlock block)
    {
        var tag = block.Kind switch
        {
            ContentBlockKind.UiText => "ui_text",
            ContentBlockKind.Prose => "prose",
            ContentBlockKind.Cta => "cta",
            ContentBlockKind.Heading => "heading",
            ContentBlockKind.ListItem => "list_item",
            ContentBlockKind.Table => "table",
            ContentBlockKind.Image => "image",
            _ => throw new ArgumentOutOfRangeException(nameof(block.Kind), block.Kind, null)
        };

        var attributes = $" id=\"{block.Id}\"";
        if (block.Level is not null) attributes += $" level=\"{block.Level}\"";

        if (block.Fragments.Count == 1 && block.Fragments[0].Source == TextSource.Visible)
            return $"<{tag}{attributes}>{Escape(block.Fragments[0].Text)}</{tag}>";

        var content = string.Join(string.Empty, block.Fragments.Select(fragment =>
        {
            var fragmentTag = fragment.Source == TextSource.Visible ? "visible_text" : "accessible_text";
            var source = fragment.Source == TextSource.Visible ? string.Empty : $" source=\"{TextSourceCode.ToCode(fragment.Source)}\"";
            return $"<{fragmentTag}{source}>{Escape(fragment.Text)}</{fragmentTag}>";
        }));
        return $"<{tag}{attributes}>{content}</{tag}>";
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}

internal sealed record ContentBlock(string Id, ContentBlockKind Kind, ElementLocation Location, IReadOnlyList<TextFragment> Fragments, int? Level = null);
internal sealed record ElementLocation(string Locator, string? Section, string Element);
internal sealed record TextFragment(TextSource Source, string Text, string Element, string? Attribute, string Locator);
internal enum ContentBlockKind { UiText, Prose, Cta, Heading, ListItem, Table, Image }
internal enum TextSource { Visible, AltText, AriaLabel, AriaLabelledBy, Placeholder, Title }

internal static class TextSourceCode
{
    public static string ToCode(TextSource source) => source switch
    {
        TextSource.Visible => "visible_text",
        TextSource.AltText => "alt_text",
        TextSource.AriaLabel => "aria_label",
        TextSource.AriaLabelledBy => "aria_labelledby",
        TextSource.Placeholder => "placeholder",
        TextSource.Title => "title",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null)
    };

    public static bool TryParse(string code, out TextSource source)
    {
        source = code switch
        {
            "visible_text" => TextSource.Visible,
            "alt_text" => TextSource.AltText,
            "aria_label" => TextSource.AriaLabel,
            "aria_labelledby" => TextSource.AriaLabelledBy,
            "placeholder" => TextSource.Placeholder,
            "title" => TextSource.Title,
            _ => default
        };
        return code is "visible_text" or "alt_text" or "aria_label" or "aria_labelledby" or "placeholder" or "title";
    }
}

internal sealed class PipelineResult { public string Url { get; init; } = ""; public string FinalUrl { get; init; } = ""; public string OutputLanguage { get; init; } = "en"; public DateTimeOffset ExecutedAt { get; init; } public CaptureResult Capture { get; init; } = new(); public MetadataResult Metadata { get; init; } = new(); public StructuralOutlineResult StructuralOutline { get; init; } = new(); public System.Text.Json.JsonElement? Analysis { get; init; } public LlmResultMetadata? Llm { get; init; } }
internal sealed class CaptureResult { public int? StatusCode { get; init; } public string? FinalUrl { get; init; } public string Html { get; init; } = ""; public string BodyText { get; init; } = ""; public int BodyWordCount { get; init; } }
internal sealed class MetadataResult { public string Title { get; init; } = ""; public string? MetaDescription { get; init; } public string CanonicalUrl { get; init; } = ""; public string Language { get; init; } = "unknown"; public string? PublishedDate { get; init; } public string? ModifiedDate { get; init; } }
internal sealed class StructuralOutlineResult { public IReadOnlyList<ContentBlock> Blocks { get; init; } = []; public string Markdown { get; init; } = ""; public StructuralOutlineSummary Summary { get; init; } = new(); }
internal sealed class StructuralOutlineSummary { public int EstimatedTokens { get; init; } public bool TruncatedByTextLimit { get; init; } }
internal sealed class LlmResultMetadata { public string Provider { get; init; } = ""; public string Model { get; init; } = ""; public string? Error { get; init; } public string[] WebSourceUrls { get; init; } = []; }
