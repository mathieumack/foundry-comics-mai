using MaiImageGenerator.Api.Configuration;
using MaiImageGenerator.Shared.Data.Entities;
using Microsoft.Extensions.Options;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MaiImageGenerator.Jobs.BoardGeneration.Services;

public sealed class ImageComposer(IOptions<FoundryOptions> options)
{
    private const int Gutter = 24;
    private const int Margin = 24;
    private const int TitleHeight = 104;
    private readonly int _imageSize = options.Value.ImageSize;

    public async Task<byte[]> BuildReferenceSheetAsync(
        IReadOnlyList<Stream> references,
        CancellationToken cancellationToken)
    {
        var columns = (int)Math.Ceiling(Math.Sqrt(references.Count));
        var rows = (int)Math.Ceiling((double)references.Count / columns);
        var tileWidth = _imageSize / columns;
        var tileHeight = _imageSize / rows;
        using var sheet = new Image<Rgba32>(_imageSize, _imageSize, Color.White);

        for (var index = 0; index < references.Count; index++)
        {
            using var reference = await Image.LoadAsync<Rgba32>(references[index], cancellationToken);
            reference.Mutate(context => context.Resize(new ResizeOptions
            {
                Size = new Size(tileWidth, tileHeight),
                Mode = ResizeMode.Max
            }));
            var x = index % columns * tileWidth + (tileWidth - reference.Width) / 2;
            var y = index / columns * tileHeight + (tileHeight - reference.Height) / 2;
            sheet.Mutate(context => context.DrawImage(reference, new Point(x, y), 1f));
        }

        await using var output = new MemoryStream();
        await sheet.SaveAsPngAsync(output, cancellationToken);
        return output.ToArray();
    }

    public async Task<byte[]> BuildBoardAsync(
        string title,
        IReadOnlyList<byte[]?> panelBytes,
        BoardTemplate template,
        CancellationToken cancellationToken)
    {
        var panels = new List<Image<Rgba32>?>(panelBytes.Count);
        try
        {
            foreach (var bytes in panelBytes)
            {
                if (bytes is null)
                {
                    panels.Add(null);
                    continue;
                }

                var panel = Image.Load<Rgba32>(bytes);
                panels.Add(panel);
            }

            var definition = BoardTemplateDefinitions.Resolve(template, panels.Count);
            var width = definition.Columns * _imageSize + (definition.Columns - 1) * Gutter + 2 * Margin;
            var height = TitleHeight + definition.Rows * _imageSize + (definition.Rows - 1) * Gutter + 2 * Margin;
            using var board = new Image<Rgba32>(width, height, Color.White);
            var font = SystemFonts.Families.First().CreateFont(52, FontStyle.Bold);
            board.Mutate(context =>
            {
                context.DrawText(title, font, Color.Black, new PointF(Margin, Margin));
                for (var index = 0; index < panels.Count; index++)
                {
                    var slot = definition.Slots[index];
                    var panelWidth = slot.ColumnSpan * _imageSize + (slot.ColumnSpan - 1) * Gutter;
                    var panelHeight = slot.RowSpan * _imageSize + (slot.RowSpan - 1) * Gutter;
                    var x = Margin + slot.Column * (_imageSize + Gutter);
                    var y = TitleHeight + Margin + slot.Row * (_imageSize + Gutter);
                    var panel = panels[index];
                    if (panel is null)
                    {
                        context.Fill(Color.LightGray, new RectangleF(x, y, panelWidth, panelHeight));
                        context.DrawText(
                            $"Panel {index + 1} was not generated",
                            SystemFonts.Families.First().CreateFont(32),
                            Color.DarkSlateGray,
                            new PointF(x + Margin, y + Margin));
                    }
                    else
                    {
                        panel.Mutate(panelContext => panelContext.Resize(panelWidth, panelHeight));
                        context.DrawImage(panel, new Point(x, y), 1f);
                    }
                }
            });

            await using var output = new MemoryStream();
            await board.SaveAsPngAsync(output, cancellationToken);
            return output.ToArray();
        }
        finally
        {
            foreach (var panel in panels)
            {
                panel?.Dispose();
            }
        }
    }
}