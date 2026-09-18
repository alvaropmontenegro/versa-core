using System.Text.Json;
using System.Text.Json.Serialization;

namespace VersaCore;

// CLI/composition only. Capture and structural snapshot extraction live in SnapshotExtractor;
// audit and fact-check behavior live in their respective pipeline files.
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<int> Main(string[] args)
    {
        await using var pipeline = new AuditPipeline();
        var repetitions = 5;

        // Match any tag or page ID. Leave empty to run every page.
        //string[] selectedTests = []; // Todas
        //string[] selectedTests = ["language_mismatch"]; // Uma tag
        //string[] selectedTests = ["intent_mismatch", "page-08"]; // Tag + página específica
        //string[] selectedTests = ["cylogy-29"];
        //string[] selectedTests = ["page-06", "page-28", "page-29"];
        //string[] selectedTests = ["page-08", "page-09"];
        //string[] selectedTests = ["page-06"];
        //string[] selectedTests = ["page-22", "page-23", "page-24"];
        string[] selectedTests = ["page-23"];

        var benchmarkFile = "beta-audit.json";
        //var benchmarkFile = "beta-audit-cylogy.json"; 
        //var benchmarkFile = "beta-audit-ethisys.json";
        var benchmarkPath = Path.Combine(AppContext.BaseDirectory, "benchmarks", benchmarkFile);

        using var benchmark = JsonDocument.Parse(await File.ReadAllTextAsync(benchmarkPath), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        var selectedPages = benchmark.RootElement.EnumerateArray().Where(test =>
            selectedTests.Length == 0 ||
            selectedTests.Contains(test.GetProperty("id").GetString(), StringComparer.OrdinalIgnoreCase) ||
            test.GetProperty("tags").EnumerateArray().Any(tag =>
                selectedTests.Contains(tag.GetString(), StringComparer.OrdinalIgnoreCase))).ToArray();

        Console.WriteLine($"Selected pages: {selectedPages.Length}/{benchmark.RootElement.GetArrayLength()}");

        var results = new List<object>();
        var resultsDirectory = Path.Combine(AppContext.BaseDirectory, "benchmarks", "results");

        Directory.CreateDirectory(resultsDirectory);
        var resultsPath = Path.Combine(resultsDirectory, $"{Path.GetFileNameWithoutExtension(benchmarkFile)}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
        var failed = 0;
        var unreviewed = 0;

        Console.WriteLine($"Results: {resultsPath}");
        Console.WriteLine($"\n");

        for (var repetition = 1; repetition <= repetitions; repetition++)
        {
            foreach (var test in selectedPages)
            {
                var url = test.GetProperty("url").GetString()!;

                Console.WriteLine($"[{repetition}/{repetitions}] {test.GetProperty("id").GetString()} — {test.GetProperty("label").GetString()}");
                Console.WriteLine(url);
                Console.WriteLine($"Expected: {test.GetProperty("expectedClassification").GetString()} | Expected Issues: {test.GetProperty("expectedIssues")}");

                AuditResult actual;
                try
                {
                    actual = await pipeline.RunAsync(url);
                }
                catch (Exception exception)
                {
                    actual = new AuditResult([], null, exception.Message);
                }

                var expectedClassification = test.GetProperty("expectedClassification").GetString();
                var reviewed = expectedClassification != "unreviewed";
                var expectedIssues = test.GetProperty("expectedIssues").EnumerateArray()
                    .Select(x => AuditIssueCode.Parse(x.GetString()!))
                    .ToArray();
                var missingIssues = reviewed ? expectedIssues.Except(actual.Issues).ToArray() : [];
                var unexpectedIssues = reviewed ? actual.Issues.Except(expectedIssues).ToArray() : [];
                var passed = actual.Error is null && actual.Classification == expectedClassification
                    && missingIssues.Length == 0 && unexpectedIssues.Length == 0;

                if (actual.Error is not null || (reviewed && !passed)) failed++;
                if (!reviewed && actual.Error is null) unreviewed++;

                results.Add(new
                {
                    id = test.GetProperty("id").GetString(),
                    label = test.GetProperty("label").GetString(),
                    description = test.GetProperty("description").GetString(),
                    url,
                    repetition,
                    tags = test.GetProperty("tags"),
                    expectedClassification,
                    actual.Classification,
                    actual.Priority,
                    expectedIssues,
                    actual.Issues,
                    missingIssues,
                    unexpectedIssues,
                    actual.LanguageSignals,
                    actual.Analysis,
                    actual.Findings,
                    actual.Error,
                    status = actual.Error is not null ? "error" : passed ? "passed" : "failed"
                });

                await File.WriteAllTextAsync(resultsPath, JsonSerializer.Serialize(results, JsonOptions));

                Console.WriteLine(actual.Error is not null ? $"ERROR: {actual.Error}" : !reviewed ? $"UNREVIEWED: {actual.Classification}; issues: {string.Join(", ", actual.Issues)}" : passed ? "PASS \n" : $"FAIL: {actual.Classification}; missing: {string.Join(", ", missingIssues)}; extra: {string.Join(", ", unexpectedIssues)} \n");
            }
        }

        Console.WriteLine($"Completed: {results.Count - failed - unreviewed} passed, {failed} failed/errors, {unreviewed} unreviewed. Results: {resultsPath}");

        return failed > 0 ? 1 : 0;
    }
}
