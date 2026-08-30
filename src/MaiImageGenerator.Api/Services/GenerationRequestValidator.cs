using MaiImageGenerator.Api.Contracts;
using MaiImageGenerator.Shared.Data.Entities;

namespace MaiImageGenerator.Api.Services;

public static class GenerationRequestValidator
{
    public static IReadOnlyDictionary<string, string[]> Validate(CreateGenerationRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            errors[nameof(request.Title)] = ["A board title is required."];
        }

        if (request.Panels.Count is < 1 or > 8 || request.Panels.Any(panel => string.IsNullOrWhiteSpace(panel.Prompt)))
        {
            errors[nameof(request.Panels)] = ["A generation requires between 1 and 8 panels with non-empty prompts."];
        }
        else if (!TemplateMatchesPanelCount(request.Template, request.Panels.Count))
        {
            errors[nameof(request.Panels)] = ["The panel count must match the selected board template."];
        }

        var characterIds = request.Characters.Select(character => character.Id).ToHashSet(StringComparer.Ordinal);
        if (request.Panels.SelectMany(panel => panel.CharacterIds).Any(characterId => !characterIds.Contains(characterId)))
        {
            errors[nameof(request.Panels)] = ["Panel characters must exist in the request."];
        }

        if (request.Characters.Any(character =>
                string.IsNullOrWhiteSpace(character.Id) ||
                string.IsNullOrWhiteSpace(character.Name) ||
                character.ReferenceIds.Any(referenceId => !Guid.TryParseExact(referenceId, "N", out _))))
        {
            errors[nameof(request.Characters)] = ["Characters must be named and reference server-issued identifiers."];
        }

        return errors;
    }

    public static IReadOnlyDictionary<string, string[]> Validate(
        CreateBoardRequest request,
        IReadOnlyCollection<string> projectCharacterIds)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            errors[nameof(request.Title)] = ["A board title is required."];
        }

        if (request.Panels.Count is < 1 or > 8 || request.Panels.Any(panel => string.IsNullOrWhiteSpace(panel.Prompt)))
        {
            errors[nameof(request.Panels)] = ["A board requires between 1 and 8 panels with non-empty prompts."];
        }
        else if (!TemplateMatchesPanelCount(request.Template, request.Panels.Count))
        {
            errors[nameof(request.Panels)] = ["The panel count must match the selected board template."];
        }
        else if (request.Panels.SelectMany(panel => panel.CharacterIds)
                 .Any(characterId => !projectCharacterIds.Contains(characterId)))
        {
            errors[nameof(request.Panels)] = ["Panel characters must exist in the project."];
        }

        return errors;
    }

    private static bool TemplateMatchesPanelCount(BoardTemplate template, int panelCount) =>
        Enum.IsDefined(template) && BoardTemplateDefinitions.Get(template).Slots.Count == panelCount;
}