namespace MaiImageGenerator.Shared.Generation;

public static class BoardInputVersions
{
    public const string PromptTemplate = "board-prompts/1";
    public const string Layout = "board-layouts/1";
    public const string RewriteInstructions = """
        You are an art director specializing in image-generation prompts. Rewrite the supplied context and panel
        description into one autonomous prompt. Preserve the requested characters, action, framing, visual style,
        displayed text, dialogue, and constraints. Return only the final prompt without Markdown or commentary.
        """;
}
