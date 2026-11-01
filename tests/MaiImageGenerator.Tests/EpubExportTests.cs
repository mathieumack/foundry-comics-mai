using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Azure.Storage.Blobs;
using MaiImageGenerator.Api.Controllers;
using MaiImageGenerator.Api.Services;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MaiImageGenerator.Tests;

public sealed class EpubExportTests
{
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    [Fact]
    public async Task BookIncludesOrderedReadableChaptersCoverMetadataAndPrivateImages()
    {
        var source = await CreateSourceAsync(ProjectType.Book);
        source.Project.Title = "A <book> & \"friends\" 🐈\u0001";
        source.Project.BookPlan!.Chapters[1].Prose = "<script>alert('no')</script> & text\nnext line\n\nSecond paragraph 🐈";
        var export = await ExportAsync(source);
        await using var stream = export.Content;

        var header = new byte[58];
        await stream.ReadExactlyAsync(header);
        Assert.Equal(0x04034b50u, BinaryPrimitives.ReadUInt32LittleEndian(header));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8, 2)));
        Assert.Equal(8, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26, 2)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28, 2)));
        Assert.Equal("mimetypeapplication/epub+zip", Encoding.ASCII.GetString(header, 30, 28));
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        Assert.Equal("mimetype", archive.Entries[0].FullName);
        Assert.Equal("application/epub+zip", Read(archive, "mimetype"));
        Assert.Contains("EPUB/package.opf", Read(archive, "META-INF/container.xml"));
        var package = Xml(archive, "EPUB/package.opf");
        Assert.Equal(source.Project.Title.Replace("\u0001", ""), package.Descendants(Dc + "title").Single().Value);
        Assert.Equal($"urn:uuid:{source.Project.Id}", package.Descendants(Dc + "identifier").Single().Value);
        Assert.Equal("und", package.Descendants(Dc + "language").Single().Value);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$",
            package.Descendants(Opf + "meta").Single(meta => (string?)meta.Attribute("property") == "dcterms:modified").Value);
        Assert.Equal(["cover", "page-1", "page-2"], package.Descendants(Opf + "itemref").Select(item => (string?)item.Attribute("idref")));
        Assert.Equal("reflowable", Layout(package));
        Assert.Single(package.Descendants(Opf + "item"), item => (string?)item.Attribute("properties") == "cover-image");
        Assert.NotNull(archive.GetEntry("EPUB/cover.xhtml"));
        Assert.Equal(["First <chapter>", "Second"], Xml(archive, "EPUB/nav.xhtml").Descendants(Xhtml + "a").Select(link => link.Value));
        var chapter = Xml(archive, "EPUB/page-1.xhtml");
        Assert.Empty(chapter.Descendants(Xhtml + "script"));
        Assert.Equal(2, chapter.Descendants(Xhtml + "p").Count());
        Assert.Single(chapter.Descendants(Xhtml + "br"));
        Assert.Contains("<script>alert('no')</script> & text", chapter.Value);
        Assert.Contains("🐈", chapter.Value);
        Assert.Equal(source.ImageBytes, Bytes(archive, "EPUB/images/page-1.png"));
        Assert.DoesNotContain("https://", string.Join("", archive.Entries.Where(entry => entry.Name.EndsWith(".xhtml")).Select(entry => Read(archive, entry.FullName))));
        Assert.DoesNotContain(source.Project.OwnerId, Read(archive, "EPUB/package.opf"));
        AssertManifestComplete(archive, package);
        Assert.Equal(2, source.ImageCalls);
        Assert.True(source.Streams.All(image => image.Disposed));
    }

    [Fact]
    public async Task BookWithoutOptionalIllustrationsStillHasReadableChaptersAndTextCover()
    {
        var source = await CreateSourceAsync(ProjectType.Book);
        source.MissingImages = true;
        var export = await ExportAsync(source);
        await using var stream = export.Content;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.DoesNotContain(archive.Entries, entry => entry.Name.EndsWith(".png"));
        Assert.Contains("Second paragraph", Xml(archive, "EPUB/page-1.xhtml").Value);
        Assert.Empty(Xml(archive, "EPUB/cover.xhtml").Descendants(Xhtml + "img"));
        AssertManifestComplete(archive, Xml(archive, "EPUB/package.opf"));
    }

    [Fact]
    public async Task ComicUsesComposedPngsInChronologicalOrderWithExactViewports()
    {
        var source = await CreateSourceAsync(ProjectType.Comic);
        var export = await ExportAsync(source);
        await using var stream = export.Content;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var package = Xml(archive, "EPUB/package.opf");
        Assert.Equal("pre-paginated", Layout(package));
        Assert.Equal(["page-1", "page-2"], package.Descendants(Opf + "itemref").Select(item => (string?)item.Attribute("idref")));
        Assert.Equal(["First board", "Second board"], Xml(archive, "EPUB/nav.xhtml").Descendants(Xhtml + "a").Select(link => link.Value));
        var page = Xml(archive, "EPUB/page-1.xhtml");
        Assert.Equal("width=640, height=960", (string?)page.Descendants(Xhtml + "meta").Single().Attribute("content"));
        var image = page.Descendants(Xhtml + "img").Single();
        Assert.Equal("640", (string?)image.Attribute("width"));
        Assert.Equal("960", (string?)image.Attribute("height"));
        Assert.Equal("width=960, height=640", (string?)Xml(archive, "EPUB/page-2.xhtml")
            .Descendants(Xhtml + "meta").Single().Attribute("content"));
        Assert.Equal(source.ImageBytes, Bytes(archive, "EPUB/images/page-1.png"));
        AssertManifestComplete(archive, package);
    }

    [Theory]
    [InlineData(ProjectType.Book)]
    [InlineData(ProjectType.Comic)]
    public async Task EmptyProjectsAreRejectedWithActionableMessage(ProjectType type)
    {
        var source = await CreateSourceAsync(type);
        source.Project.BookPlan?.Chapters.Clear();
        source.Boards.Clear();
        var error = await Assert.ThrowsAsync<EpubExportException>(() => ExportAsync(source));
        Assert.Contains("No readable", error.Message);
        Assert.Contains("retry export", error.Message);
        Assert.Equal(0, source.ImageCalls);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("prose")]
    [InlineData("image")]
    [InlineData("missing-prose")]
    [InlineData("deletion")]
    public async Task IncompleteBooksCannotBeExported(string stage)
    {
        var source = await CreateSourceAsync(ProjectType.Book);
        var plan = source.Project.BookPlan!;
        switch (stage)
        {
            case "plan": plan.Status = GenerationStatus.Pending; break;
            case "prose": plan.Chapters[0].ProseStatus = GenerationStatus.Processing; break;
            case "image": plan.Chapters[0].ImageStatus = GenerationStatus.Processing; break;
            case "missing-prose": plan.Chapters[0].Prose = null; break;
            case "deletion": source.Project.DeletionRequestedAt = DateTimeOffset.UtcNow; break;
        }
        await Assert.ThrowsAsync<EpubExportException>(() => ExportAsync(source));
        Assert.Equal(0, source.ImageCalls);
    }

    [Theory]
    [InlineData(GenerationStatus.Pending)]
    [InlineData(GenerationStatus.Processing)]
    public async Task IncompleteComicsAreRejected(GenerationStatus status)
    {
        var source = await CreateSourceAsync(ProjectType.Comic);
        source.Boards[0].Status = status;
        await Assert.ThrowsAsync<EpubExportException>(() => ExportAsync(source));
        Assert.Equal(0, source.ImageCalls);
    }

    [Theory]
    [InlineData(GenerationStatus.Failed)]
    [InlineData(GenerationStatus.Canceled)]
    public async Task TerminalBoardAttemptsDoNotBlockExportAfterSuccessfulRegeneration(GenerationStatus status)
    {
        var source = await CreateSourceAsync(ProjectType.Comic);
        source.Boards[0].Status = status;
        var export = await ExportAsync(source);
        await using (var stream = export.Content)
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            Assert.Single(Xml(archive, "EPUB/package.opf").Descendants(Opf + "itemref"));
            Assert.Equal("First board", Xml(archive, "EPUB/nav.xhtml").Descendants(Xhtml + "a").Single().Value);
        }
        source.Boards.ForEach(board => board.Status = status);
        var error = await Assert.ThrowsAsync<EpubExportException>(() => ExportAsync(source));
        Assert.Contains("No readable", error.Message);
    }

    [Fact]
    public async Task TemporaryOutputIsPrivateAndDeletedAfterDownload()
    {
        var source = await CreateSourceAsync(ProjectType.Book);
        var export = await ExportAsync(source);
        var file = Assert.IsType<FileStream>(export.Content);
        var path = file.Name;
        Assert.True(File.Exists(path));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        await file.DisposeAsync();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task MissingComposedBoardDoesNotProducePartialDownload()
    {
        var source = await CreateSourceAsync(ProjectType.Comic);
        source.MissingImages = true;
        var error = await Assert.ThrowsAsync<EpubExportException>(() => ExportAsync(source));
        Assert.Contains("Regenerate", error.Message);
    }

    [Theory]
    [InlineData(ProjectType.Book)]
    [InlineData(ProjectType.Comic)]
    public async Task ForeignOwnerGetsNoProjectOrPrivateImages(ProjectType type)
    {
        var source = await CreateSourceAsync(type);
        var result = await new EpubExportService(source).ExportAsync("other-owner", Guid.Parse(source.Project.Id), CancellationToken.None);
        Assert.Null(result);
        Assert.Equal(0, source.ImageCalls);
    }

    [Fact]
    public async Task ForeignBoardCannotExposeItsImages()
    {
        var source = await CreateSourceAsync(ProjectType.Comic);
        source.Boards[0].OwnerId = "other-owner";
        await Assert.ThrowsAsync<EpubExportException>(() => ExportAsync(source));
        Assert.Equal(0, source.ImageCalls);
    }

    [Fact]
    public async Task BlobReaderRejectsForeignOwnerAndArtifactPrefixesBeforeStorageAccess()
    {
        using var context = new MaiImageGeneratorDbContext(new DbContextOptionsBuilder<MaiImageGeneratorDbContext>().Options);
        var source = new EpubExportSource(context, new BlobServiceClient(new Uri("https://example.test")));
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<EpubExportException>(() => source.OpenImageAsync(
            "owner", id, ArtifactPathBuilder.GetGenerationBlobName("other-owner", id, "board.png"), CancellationToken.None));
        await Assert.ThrowsAsync<EpubExportException>(() => source.OpenImageAsync(
            "owner", id, ArtifactPathBuilder.GetGenerationBlobName("owner", Guid.NewGuid(), "board.png"), CancellationToken.None));
    }

    [Fact]
    public async Task CancellationIsPropagatedAndOpenedImagesAreDisposed()
    {
        var source = await CreateSourceAsync(ProjectType.Book);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExportAsync(source, cancellation.Token));
        Assert.Equal(0, source.ProjectCalls);
        using var duringCopy = new CancellationTokenSource();
        source.CancelDuringRead = duringCopy;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExportAsync(source, duringCopy.Token));
        Assert.True(source.Streams.All(stream => stream.Disposed));
        Assert.Equal(1, source.ImageCalls);
    }

    [Theory]
    [InlineData("../../<>:\"|?*\r\n", "project.epub")]
    [InlineData("My <story> / book", "My story  book.epub")]
    [InlineData("CON", "project-CON.epub")]
    public void FileNamesAreSafe(string title, string expected) =>
        Assert.Equal(expected, EpubExportService.GetFileName(title));

    [Fact]
    public async Task EndpointRequiresAuthenticationBeforeAccessingProject()
    {
        var source = await CreateSourceAsync(ProjectType.Book);
        var context = new DefaultHttpContext();
        var controller = new EpubExportsController(new HttpContextAccessor { HttpContext = context },
            new EpubExportService(source), NullLogger<EpubExportsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
        Assert.IsType<UnauthorizedResult>(await controller.Download(Guid.Parse(source.Project.Id), CancellationToken.None));
        Assert.Equal(0, source.ProjectCalls);
    }

    [Theory]
    [InlineData("private-owner", true)]
    [InlineData("other-owner", false)]
    public async Task EndpointReturnsDownloadOnlyToOwner(string ownerId, bool allowed)
    {
        var source = await CreateSourceAsync(ProjectType.Book);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"] = ownerId;
        var controller = new EpubExportsController(new HttpContextAccessor { HttpContext = context },
            new EpubExportService(source), NullLogger<EpubExportsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
        var result = await controller.Download(Guid.Parse(source.Project.Id), CancellationToken.None);
        if (allowed)
        {
            var file = Assert.IsType<FileStreamResult>(result);
            await using var stream = file.FileStream;
            Assert.Equal(EpubExportService.ContentType, file.ContentType);
            Assert.Equal("An adventure.epub", file.FileDownloadName);
            Assert.Equal("no-store", context.Response.Headers.CacheControl);
        }
        else
        {
            Assert.IsType<NotFoundResult>(result);
            Assert.Equal(0, source.ImageCalls);
        }
    }

    [Fact]
    public async Task EndpointReturnsActionableConflictForMissingContent()
    {
        var source = await CreateSourceAsync(ProjectType.Book);
        source.Project.BookPlan = null;
        var context = new DefaultHttpContext();
        context.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"] = source.Project.OwnerId;
        var controller = new EpubExportsController(new HttpContextAccessor { HttpContext = context },
            new EpubExportService(source), NullLogger<EpubExportsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
        var result = Assert.IsType<ConflictObjectResult>(
            await controller.Download(Guid.Parse(source.Project.Id), CancellationToken.None));
        Assert.Contains("Generate or save", Assert.IsType<ProblemDetails>(result.Value).Detail);
    }

    [Fact]
    public async Task HttpEndpointStreamsAnOwnerAuthorizedEpubDownload()
    {
        var source = await CreateSourceAsync(ProjectType.Comic);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IEpubExportSource>(source);
        builder.Services.AddScoped<EpubExportService>();
        builder.Services.AddControllers().AddApplicationPart(typeof(EpubExportsController).Assembly);
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var url = $"api/projects/{source.Project.Id}/export/epub";
            using var unauthenticated = await client.GetAsync(url);
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
            client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL-ID", source.Project.OwnerId);
            using var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            Assert.Equal("application/epub+zip", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("An adventure.epub", response.Content.Headers.ContentDisposition!.FileNameStar);
            Assert.True(response.Headers.CacheControl!.NoStore);
            using var archive = new ZipArchive(await response.Content.ReadAsStreamAsync());
            Assert.Equal("pre-paginated", Layout(Xml(archive, "EPUB/package.opf")));
        }
        finally { await app.StopAsync(); }
    }

    [EpubCheckTheory]
    [InlineData(ProjectType.Book, false)]
    [InlineData(ProjectType.Book, true)]
    [InlineData(ProjectType.Comic, false)]
    public async Task RepresentativeExportsPassEpubCheck(ProjectType type, bool missingImages)
    {
        var source = await CreateSourceAsync(type);
        source.MissingImages = missingImages;
        var export = await ExportAsync(source);
        await using var stream = export.Content;
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.epub");
        try
        {
            await using (var file = File.Create(path)) { await stream.CopyToAsync(file); }
            var start = new ProcessStartInfo("java") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-jar", Environment.GetEnvironmentVariable("EPUBCHECK_JAR")!, path, "--failonwarnings" })
            {
                start.ArgumentList.Add(argument);
            }
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await output + await error);
        }
        finally { File.Delete(path); }
    }

    private static string Layout(XElement package) => package.Descendants(Opf + "meta")
        .Single(meta => (string?)meta.Attribute("property") == "rendition:layout").Value;
    private static XElement Xml(ZipArchive archive, string path) => XElement.Parse(Read(archive, path));
    private static string Read(ZipArchive archive, string path) => Encoding.UTF8.GetString(Bytes(archive, path));
    private static byte[] Bytes(ZipArchive archive, string path)
    {
        using var input = archive.GetEntry(path)!.Open();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static void AssertManifestComplete(ZipArchive archive, XElement package)
    {
        var resources = package.Descendants(Opf + "item").Select(item => "EPUB/" + (string?)item.Attribute("href")).ToHashSet();
        Assert.All(package.Descendants(Opf + "item"), item => Assert.False(string.IsNullOrWhiteSpace((string?)item.Attribute("media-type"))));
        Assert.All(archive.Entries.Where(entry => entry.FullName.StartsWith("EPUB/") && entry.Name != "package.opf"),
            entry => Assert.Contains(entry.FullName, resources));
        Assert.All(resources, path => Assert.NotNull(archive.GetEntry(path)));
    }

    private static async Task<EpubExport> ExportAsync(FakeSource source, CancellationToken token = default) =>
        (await new EpubExportService(source).ExportAsync(source.Project.OwnerId, Guid.Parse(source.Project.Id), token))!;

    private static async Task<FakeSource> CreateSourceAsync(ProjectType type)
    {
        var project = new ProjectDocument { Id = Guid.NewGuid().ToString("D"), OwnerId = "private-owner", Type = type, Title = "An adventure" };
        var source = new FakeSource(project, await PngAsync(640, 960));
        if (type == ProjectType.Book)
        {
            project.BookPlan = new BookPlan
            {
                Status = GenerationStatus.Completed,
                Chapters =
                [
                    new() { Id = "second", Number = 2, Title = "Second", Prose = "Final chapter", ProseStatus = GenerationStatus.Completed, ImageStatus = GenerationStatus.Failed, ImageBlobName = "second.png" },
                    new() { Id = "first", Number = 1, Title = "First <chapter>", Prose = "A first paragraph\n\nSecond paragraph", ProseStatus = GenerationStatus.Completed, ImageStatus = GenerationStatus.Completed, ImageBlobName = "first.png" }
                ]
            };
        }
        else
        {
            source.Images["2.png"] = await PngAsync(960, 640);
            source.Boards.AddRange(new[] { 2, 1 }.Select(number => new GenerationDocument
            {
                Id = Guid.NewGuid().ToString("D"), OwnerId = project.OwnerId, ProjectId = project.Id,
                Title = number == 1 ? "First board" : "Second board", CreatedAt = DateTimeOffset.UnixEpoch.AddDays(number),
                Status = GenerationStatus.Completed, BoardBlobName = $"{number}.png"
            }));
        }
        return source;
    }

    private static async Task<byte[]> PngAsync(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        using var png = new MemoryStream();
        await image.SaveAsPngAsync(png);
        return png.ToArray();
    }

    private sealed class FakeSource(ProjectDocument project, byte[] imageBytes) : IEpubExportSource
    {
        public ProjectDocument Project { get; } = project;
        public byte[] ImageBytes { get; } = imageBytes;
        public List<GenerationDocument> Boards { get; } = [];
        public List<TrackedStream> Streams { get; } = [];
        public Dictionary<string, byte[]> Images { get; } = [];
        public int ProjectCalls { get; private set; }
        public int ImageCalls { get; private set; }
        public bool MissingImages { get; set; }
        public CancellationTokenSource? CancelDuringRead { get; set; }
        public Task<ProjectDocument?> FindProjectAsync(string ownerId, Guid projectId, CancellationToken cancellationToken)
        {
            ProjectCalls++;
            return Task.FromResult<ProjectDocument?>(Project.OwnerId == ownerId && Project.Id == projectId.ToString("D") ? Project : null);
        }
        public Task<IReadOnlyList<GenerationDocument>> ListBoardsAsync(string ownerId, string projectId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GenerationDocument>>(Boards);
        public Task<Stream?> OpenImageAsync(string ownerId, Guid artifactId, string blobName, CancellationToken cancellationToken)
        {
            Assert.Equal(Project.OwnerId, ownerId);
            Assert.True(artifactId.ToString("D") == Project.Id || Boards.Any(board => board.Id == artifactId.ToString("D")));
            ImageCalls++;
            if (MissingImages) return Task.FromResult<Stream?>(null);
            var stream = new TrackedStream(Images.GetValueOrDefault(blobName, ImageBytes), CancelDuringRead);
            Streams.Add(stream);
            return Task.FromResult<Stream?>(stream);
        }
    }

    private sealed class TrackedStream(byte[] content, CancellationTokenSource? cancellation) : MemoryStream(content)
    {
        public bool Disposed { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return base.ReadAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}

public sealed class EpubCheckTheoryAttribute : TheoryAttribute
{
    public EpubCheckTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EPUBCHECK_JAR")))
        {
            Skip = "Set EPUBCHECK_JAR to run EPUBCheck integration tests (required in EPUB export CI).";
        }
    }
}
