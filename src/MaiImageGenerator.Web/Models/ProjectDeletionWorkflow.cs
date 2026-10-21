namespace MaiImageGenerator.Web.Models;

public sealed record ProjectDeletionConfirmation(string Title, string Message, string ConfirmText);

public static class ProjectDeletionWorkflow
{
    public static async Task<bool> RunAsync(
        ProjectResponse project,
        Func<ProjectDeletionConfirmation, Task<bool>> confirmAsync,
        Func<CancellationToken, Task> deleteAsync,
        CancellationToken cancellationToken)
    {
        var format = project.Type == ProjectType.Book ? "livre illustré" : "bande dessinée";
        var confirmation = new ProjectDeletionConfirmation(
            "Suppression définitive",
            $"Supprimer définitivement « {project.Title} » ({format}) ? Toutes les données et tous les fichiers associés seront supprimés. Cette action est irréversible.",
            "Supprimer définitivement");
        if (!await confirmAsync(confirmation))
        {
            return false;
        }

        await deleteAsync(cancellationToken);
        return true;
    }
}
