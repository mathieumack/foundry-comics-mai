using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MaiImageGenerator.Shared.Data.Entities;
using SixLabors.ImageSharp;

namespace MaiImageGenerator.Tests;

public sealed class ComicBaselineTests : IDisposable
{
    private readonly string _runDirectory = Path.Combine(Path.GetTempPath(), $"comic-baseline-{Guid.NewGuid():N}");
    private static readonly string Root = FindRepositoryRoot();
    private static readonly string Pack = Path.Combine(Root, "evaluations", "comic-baseline", "v1");

    [Fact]
    public void FixedCorpus_HasTwentyCoveredBriefsAndAuthorizedPngReferences()
    {
        var corpus = ReadJson(Path.Combine(Pack, "corpus.json"));
        var briefs = corpus["briefs"]!.AsArray();
        Assert.Equal(20, briefs.Count);
        Assert.Equal(Enumerable.Range(1, 20).Select(i => $"E01-{i:00}"), briefs.Select(b => b!["id"]!.GetValue<string>()));
        var references = corpus["references"]!.AsArray();
        foreach (var reference in references)
        {
            var bytes = Convert.FromBase64String(reference!["pngBase64"]!.GetValue<string>());
            Assert.Equal(reference["sha256"]!.GetValue<string>(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
            using var image = Image.Load(bytes);
            Assert.Equal(96, image.Width);
            Assert.Equal(96, image.Height);
        }
        var referenceIds = references.Select(r => r!["id"]!.GetValue<string>()).ToHashSet();
        foreach (var brief in briefs)
        {
            Assert.InRange(brief!["story"]!.GetValue<string>().Length, 1, 10000);
            Assert.NotEmpty(brief["expectedBeats"]!.AsArray());
            foreach (var reference in brief["referenceIds"]!.AsArray())
                Assert.Contains(reference!.GetValue<string>(), referenceIds);
            var layout = Enum.Parse<BoardTemplate>(brief["layout"]!.GetValue<string>());
            if (brief["route"]!.GetValue<string>() == "free-form")
                Assert.Equal(BoardTemplateDefinitions.Get(layout).Slots.Count, brief["panelPrompts"]!.AsArray().Count);
        }
        var tags = briefs.SelectMany(b => b!["tags"]!.AsArray()).Select(t => t!.GetValue<string>()).ToHashSet();
        foreach (var tag in new[] { "silent-storytelling", "dialogue", "recurring-characters", "spatial-continuity", "reveals", "dense-text", "intentional-stylistic-exceptions", "free-form" })
            Assert.Contains(tag, tags);
        var rubric = ReadJson(Path.Combine(Pack, "rubric.json"))["dimensions"]!.AsObject();
        Assert.Equal(new[] { "storyClarity", "characterIdentity", "continuity", "readingOrder" }, rubric.Select(d => d.Key));
        foreach (var dimension in rubric)
            Assert.Equal(new[] { "1", "2", "3", "4", "5" }, dimension.Value!.AsObject().Select(a => a.Key));
        var status = ReadJson(Path.Combine(Pack, "baseline-status.json"));
        Assert.Equal("blocked", status["status"]!.GetValue<string>());
        Assert.Equal(briefs.Select(b => b!["id"]!.GetValue<string>()), status["reports"]!.AsArray().Select(r => r!["briefId"]!.GetValue<string>()));
        Assert.All(status["reports"]!.AsArray(), r =>
        {
            Assert.Null(r!["outputVersion"]);
            Assert.Null(r["configurationVersion"]);
            Assert.Null(r["humanRatings"]);
        });
    }

    [Fact]
    public async Task Initialize_AndSummarize_PreserveUnknownsAndNeverOverwriteEvidence()
    {
        await Initialize();
        var summary = await Summarize();
        Assert.Equal(20, summary["pendingEvidenceBriefIds"]!.AsArray().Count);
        Assert.False(summary["configurationComplete"]!.GetValue<bool>());
        Assert.False(summary["ceilingApprovalRecorded"]!.GetValue<bool>());
        var image = Stage(summary, "image");
        Assert.Null(image["requestCount"]);
        Assert.Null(image["retryCount"]);
        Assert.Null(image["p50WallDurationMs"]);
        Assert.Null(image["p95WallDurationMs"]);
        Assert.Null(image["cost"]);
        Assert.Equal(0, image["capturedInvocationCount"]!.GetValue<int>());
        Assert.Null(image["reportedUsage"]!["totalTokens"]!["reportedSubtotal"]);
        Assert.Null(summary["editorial"]!["storyClarity"]!["median"]);
        Assert.Equal(0, Stage(summary, "composition")["requestCount"]!.GetValue<int>());
        var before = File.ReadAllText(RunPath);
        var repeat = await Invoke("-Initialize");
        Assert.NotEqual(0, repeat.ExitCode);
        Assert.Contains("never overwritten", repeat.Error);
        Assert.Equal(before, File.ReadAllText(RunPath));
    }

    [Fact]
    public async Task Summarize_ReportsNearestRankStageDurationsAndIndependentUsageCoverage()
    {
        await Initialize();
        var run = ReadJson(RunPath);
        for (var i = 0; i < 20; i++)
        {
            var stage = ReportStage(run, i, "image");
            stage["state"] = i == 19 ? "failed" : "completed";
            stage["wallDurationMs"] = (i + 1) * 100;
            stage["requestCount"] = 1;
            stage["retryCount"] = 0;
            stage["measurementEvidence"] = "Synthetic deterministic test measurements, not live baseline data.";
            stage["traces"]!.AsArray().Add(JsonNode.Parse("""
                {"operation":"panel-image","provider":"test","model":"test-image","usageSource":"provider-reported",
                 "inputTokens":0,"outputTokens":7,"totalTokens":99,"durationMs":10,"status":"completed"}
                """));
        }
        ReportStage(run, 19, "image")["traces"]![0]!["inputTokens"] = null;
        ReportStage(run, 19, "image")["traces"]![0]!["totalTokens"] = null;
        AddRating(run, "rater-a", 2);
        AddRating(run, "rater-b", 5);
        Save(run);
        var summary = await Summarize();
        var image = Stage(summary, "image");
        Assert.Equal(1000, image["p50WallDurationMs"]!.GetValue<int>());
        Assert.Equal(1900, image["p95WallDurationMs"]!.GetValue<int>());
        Assert.Equal(20, image["durationSampleCount"]!.GetValue<int>());
        Assert.Equal(20, image["requestCount"]!.GetValue<int>());
        Assert.Equal(0, image["retryCount"]!.GetValue<int>());
        var usage = image["reportedUsage"]!;
        Assert.Equal(0, usage["inputTokens"]!["reportedSubtotal"]!.GetValue<int>());
        Assert.Null(usage["inputTokens"]!["completeTotal"]);
        Assert.Equal(140, usage["outputTokens"]!["completeTotal"]!.GetValue<int>());
        Assert.Equal(1881, usage["totalTokens"]!["reportedSubtotal"]!.GetValue<int>());
        Assert.Null(usage["totalTokens"]!["completeTotal"]);
        Assert.Equal(3.5, summary["editorial"]!["storyClarity"]!["median"]!.GetValue<double>());
        Assert.Null(image["cost"]);
    }

    [Fact]
    public async Task Summarize_DoesNotTreatInvocationCountOrEstimatedUsageAsCompleteProviderUsage()
    {
        await Initialize();
        var run = ReadJson(RunPath);
        var stage = ReportStage(run, 0, "rewrite");
        stage["state"] = "completed";
        stage["traces"]!.AsArray().Add(JsonNode.Parse("""
            {"operation":"prompt-rewrite","provider":"test","model":"test-text","usageSource":"estimated",
             "inputTokens":12,"outputTokens":7,"totalTokens":19,"durationMs":10,"status":"completed"}
            """));
        Save(run);
        var rewrite = Stage(await Summarize(), "rewrite");
        Assert.Equal(1, rewrite["capturedInvocationCount"]!.GetValue<int>());
        Assert.Null(rewrite["requestCount"]);
        Assert.Null(rewrite["reportedUsage"]!["totalTokens"]!["reportedSubtotal"]);
        Assert.Null(rewrite["reportedUsage"]!["totalTokens"]!["completeTotal"]);
    }

    [Fact]
    public async Task Summarize_PreservesLargeProviderCountersWithoutFloatingPointRounding()
    {
        await Initialize();
        var run = ReadJson(RunPath);
        foreach (var report in run["reports"]!.AsArray())
        {
            var stage = Stage(report!.AsObject(), "image");
            stage["requestCount"] = 0;
            stage["measurementEvidence"] = "Known absence of execution in this deterministic test.";
        }
        var image = ReportStage(run, 0, "image");
        image["state"] = "completed";
        image["requestCount"] = 1;
        image["traces"]!.AsArray().Add(JsonNode.Parse("""
            {"operation":"panel-image","provider":"test","model":"test-image","usageSource":"provider-reported",
             "inputTokens":9007199254740993,"outputTokens":0,"totalTokens":null,"durationMs":10,"status":"completed"}
            """));
        Save(run);
        var usage = Stage(await Summarize(), "image")["reportedUsage"]!;
        Assert.Equal(9007199254740993L, usage["inputTokens"]!["completeTotal"]!.GetValue<long>());
        Assert.Equal(0, usage["outputTokens"]!["completeTotal"]!.GetValue<int>());
        Assert.Null(usage["totalTokens"]!["completeTotal"]);
    }

    [Fact]
    public async Task Summarize_AcceptsHashedCaseEvidenceWithoutConfusingItWithWholeCorpusCompletion()
    {
        await Initialize();
        var run = ReadJson(RunPath);
        run["reports"]![0]!["technicalOutcome"] = "succeeded";
        foreach (var kind in new[] { "approved-input", "generation-response", "configuration", "board", "panel", "panel", "panel" })
        {
            var relativePath = $"E01-01/{Guid.NewGuid():N}.dat";
            var path = Path.Combine(_runDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"Synthetic test artifact: {kind}");
            run["reports"]![0]!["artifacts"]!.AsArray().Add(new JsonObject
            {
                ["path"] = relativePath, ["kind"] = kind,
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))
            });
        }
        AddRating(run, "rater-a", 3);
        AddRating(run, "rater-b", 4);
        Save(run);
        var summary = await Summarize();
        Assert.Equal(19, summary["pendingEvidenceBriefIds"]!.AsArray().Count);
        Assert.DoesNotContain(summary["pendingEvidenceBriefIds"]!.AsArray(), id => id!.GetValue<string>() == "E01-01");
        Assert.False(summary["configurationComplete"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("duplicate-brief")]
    [InlineData("duplicate-stage")]
    [InlineData("corpus-drift")]
    [InlineData("unavailable-zero")]
    [InlineData("invalid-rating")]
    [InlineData("duplicate-rater")]
    [InlineData("traversal")]
    [InlineData("artifact-hash")]
    [InlineData("reference-hash")]
    [InlineData("wrong-operation")]
    [InlineData("missing-count-evidence")]
    [InlineData("unexecuted-count")]
    [InlineData("composition-count")]
    [InlineData("symlink")]
    [InlineData("duplicate-ceiling")]
    [InlineData("request-below-traces")]
    public async Task Summarize_RejectsInvalidOrMisleadingEvidence(string fault)
    {
        await Initialize();
        var run = ReadJson(RunPath);
        var report = run["reports"]![0]!;
        var stage = ReportStage(run, 0, "image");
        switch (fault)
        {
            case "duplicate-brief": run["reports"]![1]!["briefId"] = "E01-01"; break;
            case "duplicate-stage": ReportStage(run, 0, "rewrite")["name"] = "image"; break;
            case "corpus-drift": run["corpusSha256"] = new string('0', 64); break;
            case "invalid-rating": AddRating(run, "rater-a", 6); break;
            case "duplicate-rater": AddRating(run, "rater-a", 3); AddRating(run, "rater-a", 4); break;
            case "reference-hash": File.AppendAllText(Path.Combine(_runDirectory, "references", "pip.png"), "changed"); break;
            case "traversal":
            case "artifact-hash":
                report["artifacts"]!.AsArray().Add(new JsonObject
                {
                    ["path"] = fault == "traversal" ? "../outside.json" : "references/pip.png",
                    ["sha256"] = new string('0', 64), ["kind"] = "panel"
                });
                break;
            case "missing-count-evidence": stage["requestCount"] = 2; break;
            case "unexecuted-count":
                stage["requestCount"] = 2;
                stage["measurementEvidence"] = "Cannot claim calls from an unexecuted stage.";
                break;
            case "composition-count": ReportStage(run, 0, "composition")["requestCount"] = 2; break;
            case "request-below-traces":
                stage["requestCount"] = 0;
                stage["measurementEvidence"] = "Cannot claim no calls while providing a trace.";
                goto default;
            case "duplicate-ceiling":
                run["ceilingApproval"] = JsonNode.Parse("""
                    {"decisionReference":"synthetic-test","stages":[
                    {"name":"image","maxRequestsPerBrief":6,"maxP95DurationMs":1000},
                    {"name":"image","maxRequestsPerBrief":6,"maxP95DurationMs":1000},
                    {"name":"image","maxRequestsPerBrief":6,"maxP95DurationMs":1000},
                    {"name":"image","maxRequestsPerBrief":6,"maxP95DurationMs":1000}]}
                    """);
                break;
            case "symlink":
                File.CreateSymbolicLink(Path.Combine(_runDirectory, "linked.png"), Path.Combine(_runDirectory, "references", "pip.png"));
                report["artifacts"]!.AsArray().Add(new JsonObject
                {
                    ["path"] = "linked.png", ["kind"] = "panel",
                    ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(_runDirectory, "references", "pip.png"))))
                });
                break;
            default:
                stage["state"] = "completed";
                stage["traces"]!.AsArray().Add(JsonNode.Parse($$"""
                    {"operation":"{{(fault == "wrong-operation" ? "prompt-rewrite" : "panel-image")}}",
                     "provider":"test","model":"test","usageSource":"unavailable",
                     "inputTokens":0,"outputTokens":null,"totalTokens":null,"durationMs":1,"status":"completed"}
                    """));
                break;
        }
        Save(run);
        var result = await Invoke();
        Assert.NotEqual(0, result.ExitCode);
    }

    private string RunPath => Path.Combine(_runDirectory, "run.json");
    private async Task Initialize()
    {
        var result = await Invoke("-Initialize");
        Assert.True(result.ExitCode == 0, result.Error);
    }

    private async Task<JsonObject> Summarize()
    {
        var result = await Invoke();
        Assert.True(result.ExitCode == 0, result.Error);
        return JsonNode.Parse(result.Output)!.AsObject();
    }

    private async Task<(int ExitCode, string Output, string Error)> Invoke(params string[] arguments)
    {
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-File", Path.Combine(Root, "scripts", "comic-baseline.ps1"), "-RunDirectory", _runDirectory }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output, await error);
    }

    private void Save(JsonObject run) => File.WriteAllText(RunPath, run.ToJsonString());
    private static JsonObject ReadJson(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    private static JsonObject Stage(JsonObject summary, string name) =>
        summary["stages"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == name)!.AsObject();
    private static JsonObject ReportStage(JsonObject run, int index, string name) => Stage(run["reports"]![index]!.AsObject(), name);
    private static void AddRating(JsonObject run, string evaluator, int score)
    {
        var scores = new JsonObject();
        var evidence = new JsonObject();
        foreach (var dimension in new[] { "storyClarity", "characterIdentity", "continuity", "readingOrder" })
        {
            scores[dimension] = score;
            evidence[dimension] = "Panel 2: synthetic test annotation, not a human baseline rating.";
        }
        run["reports"]![0]!["ratings"]!.AsArray().Add(new JsonObject { ["evaluator"] = evaluator, ["scores"] = scores, ["evidence"] = evidence });
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "evaluations", "comic-baseline")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Cannot find baseline corpus.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_runDirectory))
            Directory.Delete(_runDirectory, recursive: true);
    }
}
