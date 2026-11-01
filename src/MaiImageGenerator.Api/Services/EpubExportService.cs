using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using MaiImageGenerator.Shared.Data.Entities;
using MaiImageGenerator.Shared.Services;

namespace MaiImageGenerator.Api.Services;

public sealed class EpubExportException(string message) : Exception(message);

public sealed record EpubExport(Stream Content, string FileName);

public sealed class EpubExportService(IEpubExportSource source)
{
    public const string ContentType = "application/epub+zip";
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Epub = "http://www.idpf.org/2007/ops";

    public async Task<EpubExport?> ExportAsync(string ownerId, Guid projectId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = await source.FindProjectAsync(ownerId, projectId, cancellationToken);
        if (project is null || project.OwnerId != ownerId || project.Id != projectId.ToString("D"))
        {
            return null;
        }

        if (project.DeletionRequestedAt is not null)
        {
            throw new EpubExportException("Project deletion is pending. Finish deletion before exporting.");
        }

        var pages = new List<Page>();
        if (project.Type == ProjectType.Book)
        {
            var plan = project.BookPlan;
            if (plan is null || plan.Chapters.Count == 0 || plan.Chapters.All(chapter => string.IsNullOrWhiteSpace(chapter.Prose)))
            {
                throw new EpubExportException("No readable chapters are available. Generate or save chapter prose, then retry export.");
            }

            if (plan.Status is GenerationStatus.Pending or GenerationStatus.Processing ||
                BookImageGenerationLease.IsActive(project, DateTimeOffset.UtcNow) ||
                plan.Chapters.Any(chapter => chapter.ProseStatus == GenerationStatus.Processing ||
                    chapter.ImageStatus == GenerationStatus.Processing || string.IsNullOrWhiteSpace(chapter.Prose)))
            {
                throw new EpubExportException("Finish generating and save prose for every chapter, then retry export.");
            }

            pages.AddRange(plan.Chapters.OrderBy(chapter => chapter.Number)
                .Select(chapter => new Page(chapter.Title, chapter.Prose, chapter.ImageBlobName, projectId)));
        }
        else if (project.Type == ProjectType.Comic)
        {
            var boards = await source.ListBoardsAsync(ownerId, project.Id, cancellationToken);
            if (boards.Count == 0)
            {
                throw new EpubExportException("No readable boards are available. Generate a completed board, then retry export.");
            }

            if (boards.Any(board => board.OwnerId != ownerId || board.ProjectId != project.Id))
            {
                throw new EpubExportException("A board does not belong to this project.");
            }

            if (boards.Any(board => board.Status != GenerationStatus.Completed || string.IsNullOrWhiteSpace(board.BoardBlobName)))
            {
                throw new EpubExportException("Finish or retry incomplete boards before exporting the comic.");
            }

            pages.AddRange(boards.OrderBy(board => board.CreatedAt).ThenBy(board => board.Id, StringComparer.Ordinal)
                .Select(board => new Page(board.Title, null, board.BoardBlobName, Guid.Parse(board.Id))));
        }
        else
        {
            throw new EpubExportException("This project format cannot be exported.");
        }

        // Spool to an automatically deleted file so private images do not accumulate in API memory.
        var output = new FileStream(
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            await WriteAsync(output, project, pages, cancellationToken);
            output.Position = 0;
            return new EpubExport(output, GetFileName(project.Title));
        }
        catch
        {
            await output.DisposeAsync();
            throw;
        }
    }

    private async Task WriteAsync(Stream output, ProjectDocument project, List<Page> pages, CancellationToken token)
    {
        var comic = project.Type == ProjectType.Comic;
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
        await WriteTextAsync(archive, "mimetype", ContentType, token, CompressionLevel.NoCompression);
        XNamespace container = "urn:oasis:names:tc:opendocument:xmlns:container";
        await WriteXmlAsync(archive, "META-INF/container.xml",
            new XElement(container + "container", new XAttribute("version", "1.0"),
                new XElement(container + "rootfiles", new XElement(container + "rootfile",
                    new XAttribute("full-path", "EPUB/package.opf"),
                    new XAttribute("media-type", "application/oebps-package+xml")))), token);

        var manifest = new XElement(Opf + "manifest",
            Item("nav", "nav.xhtml", "application/xhtml+xml", "nav"),
            Item("style", "style.css", "text/css"));
        var spine = new XElement(Opf + "spine", new XAttribute("page-progression-direction", "ltr"));
        var navigation = new XElement(Xhtml + "ol");
        string? coverImage = null;
        for (var index = 0; index < pages.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var page = pages[index];
            var id = $"page-{index + 1}";
            var imagePath = $"images/{id}.png";
            var hasImage = false;
            var width = 0;
            var height = 0;
            if (page.BlobName is not null)
            {
                await using var image = await source.OpenImageAsync(project.OwnerId, page.ArtifactId, page.BlobName, token);
                if (image is not null)
                {
                    var header = new byte[24];
                    await image.ReadExactlyAsync(header, token);
                    if (!header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                        BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8, 4)) != 13 ||
                        !header.AsSpan(12, 4).SequenceEqual("IHDR"u8))
                    {
                        throw new EpubExportException("An illustration is not a PNG image. Regenerate it, then retry export.");
                    }
                    width = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4));
                    height = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4));
                    if (width <= 0 || height <= 0)
                    {
                        throw new EpubExportException("An illustration has invalid dimensions. Regenerate it, then retry export.");
                    }
                    var entry = archive.CreateEntry($"EPUB/{imagePath}", CompressionLevel.NoCompression);
                    await using (var destination = entry.Open())
                    {
                        await destination.WriteAsync(header, token);
                        await image.CopyToAsync(destination, token);
                    }
                    hasImage = true;
                    manifest.Add(Item($"image-{index + 1}", imagePath, "image/png", coverImage is null ? "cover-image" : null));
                    coverImage ??= imagePath;
                }
            }

            if (comic && !hasImage)
            {
                throw new EpubExportException("A composed board image is missing. Regenerate that board, then retry export.");
            }

            var title = string.IsNullOrWhiteSpace(page.Title) ? $"Page {index + 1}" : page.Title;
            var body = new XElement(Xhtml + "body");
            if (comic)
            {
                body.Add(new XAttribute("class", "comic"),
                    new XElement(Xhtml + "img", new XAttribute("src", imagePath),
                        new XAttribute("alt", Text(title)), new XAttribute("width", width), new XAttribute("height", height)));
            }
            else
            {
                body.Add(new XElement(Xhtml + "h1", Text(title)));
                if (hasImage)
                {
                    body.Add(new XElement(Xhtml + "img", new XAttribute("src", imagePath),
                        new XAttribute("alt", Text($"Illustration: {title}"))));
                }

                foreach (var paragraph in (page.Prose ?? "").ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
                {
                    var element = new XElement(Xhtml + "p");
                    foreach (var line in paragraph.Split('\n'))
                    {
                        if (!element.IsEmpty)
                        {
                            element.Add(new XElement(Xhtml + "br"));
                        }
                        element.Add(new XText(Text(line)));
                    }
                    body.Add(element);
                }
            }

            await WriteXmlAsync(archive, $"EPUB/{id}.xhtml", Document(title, body,
                comic ? $"width={width.ToString(CultureInfo.InvariantCulture)}, height={height.ToString(CultureInfo.InvariantCulture)}" : null), token);
            manifest.Add(Item(id, $"{id}.xhtml", "application/xhtml+xml"));
            spine.Add(new XElement(Opf + "itemref", new XAttribute("idref", id)));
            navigation.Add(new XElement(Xhtml + "li",
                new XElement(Xhtml + "a", new XAttribute("href", $"{id}.xhtml"), Text(title))));
        }

        if (!comic)
        {
            var cover = new XElement(Xhtml + "body", new XAttribute(Epub + "type", "cover"),
                new XElement(Xhtml + "h1", Text(project.Title)));
            if (coverImage is not null)
            {
                cover.Add(new XElement(Xhtml + "img", new XAttribute("src", coverImage),
                    new XAttribute("alt", Text(project.Title))));
            }
            await WriteXmlAsync(archive, "EPUB/cover.xhtml", Document(project.Title, cover), token);
            manifest.Add(Item("cover", "cover.xhtml", "application/xhtml+xml"));
            spine.AddFirst(new XElement(Opf + "itemref", new XAttribute("idref", "cover")));
        }

        var navBody = new XElement(Xhtml + "body",
            new XElement(Xhtml + "nav", new XAttribute(Epub + "type", "toc"),
                new XAttribute("id", "toc"), new XElement(Xhtml + "h1", "Contents"), navigation));
        await WriteXmlAsync(archive, "EPUB/nav.xhtml", Document("Contents", navBody), token);
        await WriteTextAsync(archive, "EPUB/style.css",
            "body { font-family: serif; line-height: 1.5; } img { max-width: 100%; height: auto; } " +
            "body.comic { margin: 0; padding: 0; line-height: 0; } body.comic img { display: block; width: 100%; height: 100%; }", token);

        var metadata = new XElement(Opf + "metadata", new XAttribute(XNamespace.Xmlns + "dc", Dc),
            new XElement(Dc + "identifier", new XAttribute("id", "publication-id"), $"urn:uuid:{project.Id}"),
            new XElement(Dc + "title", Text(project.Title)),
            new XElement(Dc + "language", "und"),
            new XElement(Opf + "meta", new XAttribute("property", "dcterms:modified"),
                DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
            new XElement(Opf + "meta", new XAttribute("property", "rendition:layout"), comic ? "pre-paginated" : "reflowable"));
        if (comic)
        {
            metadata.Add(new XElement(Opf + "meta", new XAttribute("property", "rendition:spread"), "none"));
        }
        await WriteXmlAsync(archive, "EPUB/package.opf",
            new XElement(Opf + "package", new XAttribute("version", "3.0"),
                new XAttribute("unique-identifier", "publication-id"),
                new XAttribute("prefix", "rendition: http://www.idpf.org/vocab/rendition/#"),
                metadata, manifest, spine), token);
        token.ThrowIfCancellationRequested();
    }

    private static XElement Document(string title, XElement body, string? viewport = null) =>
        new(Xhtml + "html", new XAttribute(XNamespace.Xmlns + "epub", Epub),
            new XAttribute(XNamespace.Xml + "lang", "und"), new XAttribute("lang", "und"),
            new XElement(Xhtml + "head", new XElement(Xhtml + "title", Text(title)),
                new XElement(Xhtml + "link", new XAttribute("rel", "stylesheet"), new XAttribute("href", "style.css")),
                viewport is null ? null : new XElement(Xhtml + "meta", new XAttribute("name", "viewport"),
                    new XAttribute("content", viewport))), body);

    private static XElement Item(string id, string href, string mediaType, string? properties = null) =>
        new(Opf + "item", new XAttribute("id", id), new XAttribute("href", href),
            new XAttribute("media-type", mediaType), properties is null ? null : new XAttribute("properties", properties));

    private static string Text(string value) =>
        string.Concat(value.EnumerateRunes().Where(rune =>
            rune.Value is 9 or 10 or 13 || rune.Value is >= 0x20 and <= 0xD7FF ||
            rune.Value is >= 0xE000 and <= 0xFFFD || rune.Value is >= 0x10000 and <= 0x10FFFF));

    public static string GetFileName(string title)
    {
        var name = new string(title.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or ' ')
            .Take(100).ToArray()).Trim();
        if (name.Length == 0)
        {
            name = "project";
        }
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            name = $"project-{name}";
        }
        return $"{name}.epub";
    }

    private static Task WriteXmlAsync(ZipArchive archive, string path, XElement document, CancellationToken token) =>
        WriteTextAsync(archive, path, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + document.ToString(SaveOptions.DisableFormatting), token);

    private static async Task WriteTextAsync(
        ZipArchive archive, string path, string content, CancellationToken token, CompressionLevel compression = CompressionLevel.Optimal)
    {
        token.ThrowIfCancellationRequested();
        var entry = archive.CreateEntry(path, compression);
        await using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), token);
    }

    private sealed record Page(string Title, string? Prose, string? BlobName, Guid ArtifactId);
}
