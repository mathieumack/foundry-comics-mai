namespace MaiImageGenerator.Web.Models;

public enum BoardTemplate
{
    Standard,
    Storyline,
    Dynamic1,
    Dynamic2
}

public sealed record BoardTemplateSlot(int Row, int Column, int ColumnSpan = 1, int RowSpan = 1);

public sealed record BoardTemplateDefinition(
    int Rows,
    int Columns,
    IReadOnlyList<BoardTemplateSlot> Slots);

public static class BoardTemplateDefinitions
{
    public static BoardTemplateDefinition Get(BoardTemplate template) => template switch
    {
        BoardTemplate.Standard => new(3, 2,
        [
            new(0, 0), new(0, 1),
            new(1, 0), new(1, 1),
            new(2, 0), new(2, 1)
        ]),
        BoardTemplate.Storyline => new(1, 3,
        [
            new(0, 0), new(0, 1), new(0, 2)
        ]),
        BoardTemplate.Dynamic1 => new(3, 2,
        [
            new(0, 0), new(0, 1),
            new(1, 0, 2),
            new(2, 0), new(2, 1)
        ]),
        BoardTemplate.Dynamic2 => new(3, 2,
        [
            new(0, 0, 2),
            new(1, 0), new(1, 1),
            new(2, 0), new(2, 1)
        ]),
        _ => throw new ArgumentOutOfRangeException(nameof(template))
    };

    public static BoardTemplateDefinition Resolve(BoardTemplate template, int panelCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(panelCount, 1);
        var definition = Get(template);
        if (definition.Slots.Count == panelCount)
        {
            return definition;
        }

        var columns = Math.Min(panelCount, 2);
        var rows = (int)Math.Ceiling((double)panelCount / columns);
        return new(rows, columns, Enumerable.Range(0, panelCount)
            .Select(index => new BoardTemplateSlot(index / columns, index % columns))
            .ToArray());
    }
}
