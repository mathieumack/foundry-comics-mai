using MaiImageGenerator.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MaiImageGenerator.Shared.Data.DatabaseContext;

public sealed class MaiImageGeneratorDbContext(
    DbContextOptions<MaiImageGeneratorDbContext> options) : DbContext(options)
{
    public DbSet<GenerationDocument> Generations => Set<GenerationDocument>();
    public DbSet<ProjectDocument> Projects => Set<ProjectDocument>();
    public DbSet<ReferenceDocument> References => Set<ReferenceDocument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var generation = modelBuilder.Entity<GenerationDocument>();
        generation.ToContainer("documents");
        generation.HasKey(item => item.Id);
        generation.HasPartitionKey(item => item.OwnerId);
        generation.HasNoDiscriminator();
        generation.Property(item => item.Id).ToJsonProperty("id");
        generation.Property(item => item.OwnerId).ToJsonProperty("ownerId");
        generation.Property(item => item.ProjectId).ToJsonProperty("projectId");
        generation.Property(item => item.SchemaVersion).ToJsonProperty("schemaVersion");
        generation.Property(item => item.Title).ToJsonProperty("title");
        generation.Property(item => item.Description).ToJsonProperty("description");
        generation.Property(item => item.Context).ToJsonProperty("context");
        generation.Property(item => item.RewritePrompt).ToJsonProperty("rewritePrompt");
        generation.Property(item => item.ScaffoldId).ToJsonProperty("scaffoldId");
        generation.OwnsMany(item => item.ScaffoldTraces, trace => ConfigureTrace(trace, "scaffoldTraces"));
        generation.Property(item => item.Status).HasConversion<string>().ToJsonProperty("status");
        generation.Property(item => item.BoardBlobName).ToJsonProperty("boardBlobName");
        generation.Property(item => item.Error).ToJsonProperty("error");
        generation.Property(item => item.CreatedAt).ToJsonProperty("createdAt");
        generation.Property(item => item.UpdatedAt).ToJsonProperty("updatedAt");
        generation.Property(item => item.ETag).ToJsonProperty("_etag").IsETagConcurrency();
        generation.OwnsMany(item => item.Panels, panel =>
        {
            panel.ToJsonProperty("panels");
            panel.Property(item => item.Status).HasConversion<string>();
            panel.OwnsMany(item => item.Traces, trace => ConfigureTrace(trace, "traces"));
        });
        var project = modelBuilder.Entity<ProjectDocument>();
        project.ToContainer("projects");
        project.HasKey(item => item.Id);
        project.HasPartitionKey(item => item.OwnerId);
        project.HasNoDiscriminator();
        project.Property(item => item.Id).ToJsonProperty("id");
        project.Property(item => item.OwnerId).ToJsonProperty("ownerId");
        project.Property(item => item.SchemaVersion).ToJsonProperty("schemaVersion");
        project.Property(item => item.Type).HasConversion<string>().ToJsonProperty("type");
        project.Property(item => item.Title).ToJsonProperty("title");
        project.Property(item => item.Context).ToJsonProperty("context");
        project.Property(item => item.SourceIdea).ToJsonProperty("sourceIdea");
        project.Property(item => item.ScaffoldId).ToJsonProperty("scaffoldId");
        project.OwnsMany(item => item.ScaffoldTraces, trace => ConfigureTrace(trace, "scaffoldTraces"));
        project.Property(item => item.CreatedAt).ToJsonProperty("createdAt");
        project.Property(item => item.UpdatedAt).ToJsonProperty("updatedAt");
        project.Property(item => item.DeletionRequestedAt).ToJsonProperty("deletionRequestedAt");
        project.Property(item => item.ETag).ToJsonProperty("_etag").IsETagConcurrency();
        project.OwnsMany(item => item.Characters, character =>
        {
            character.ToJsonProperty("characters");
            character.OwnsMany(item => item.References, reference =>
            {
                reference.ToJsonProperty("references");
            });
        });
        project.OwnsOne(item => item.BookPlan, plan =>
        {
            plan.ToJsonProperty("bookPlan");
            plan.Property(item => item.GenerationRequestId).ToJsonProperty("generationRequestId");
            plan.Property(item => item.Status).HasConversion<string>();
            plan.Property(item => item.ImageGenerationRequestId).ToJsonProperty("imageGenerationRequestId");
            plan.Property(item => item.ImageGenerationStartedAt).ToJsonProperty("imageGenerationStartedAt");
            plan.OwnsMany(item => item.Traces, trace => ConfigureTrace(trace, "traces"));
            plan.OwnsMany(item => item.Chapters, chapter =>
            {
                chapter.ToJsonProperty("chapters");
                chapter.Property(item => item.ProseStatus).HasConversion<string>();
                chapter.Property(item => item.ImageStatus).HasConversion<string>();
                chapter.Property(item => item.ImageGenerationRequestId).ToJsonProperty("imageGenerationRequestId");
                chapter.Property(item => item.ImageGenerationStartedAt).ToJsonProperty("imageGenerationStartedAt");
                chapter.OwnsMany(item => item.ProseTraces, trace => ConfigureTrace(trace, "proseTraces"));
                chapter.OwnsMany(item => item.ImageTraces, trace => ConfigureTrace(trace, "imageTraces"));
            });
        });

        var reference = modelBuilder.Entity<ReferenceDocument>();
        reference.ToContainer("references");
        reference.HasKey(item => item.Id);
        reference.HasPartitionKey(item => item.OwnerId);
        reference.HasNoDiscriminator();
        reference.Property(item => item.Id).ToJsonProperty("id");
        reference.Property(item => item.OwnerId).ToJsonProperty("ownerId");
        reference.Property(item => item.SchemaVersion).ToJsonProperty("schemaVersion");
        reference.Property(item => item.FileName).ToJsonProperty("fileName");
        reference.Property(item => item.ContentType).ToJsonProperty("contentType");
        reference.Property(item => item.Length).ToJsonProperty("length");
        reference.Property(item => item.BlobName).ToJsonProperty("blobName");
        reference.Property(item => item.CreatedAt).ToJsonProperty("createdAt");
        reference.Property(item => item.UpdatedAt).ToJsonProperty("updatedAt");
        reference.Property(item => item.ETag).ToJsonProperty("_etag").IsETagConcurrency();
    }

    private static void ConfigureTrace<TOwner>(
        OwnedNavigationBuilder<TOwner, AiGenerationTrace> trace,
        string jsonProperty) where TOwner : class
    {
        trace.ToJsonProperty(jsonProperty);
        trace.Property(item => item.Operation).ToJsonProperty("operation");
        trace.Property(item => item.Provider).ToJsonProperty("provider");
        trace.Property(item => item.Model).ToJsonProperty("model");
        trace.Property(item => item.RequestId).ToJsonProperty("requestId");
        trace.Property(item => item.InputTokens).ToJsonProperty("inputTokens");
        trace.Property(item => item.OutputTokens).ToJsonProperty("outputTokens");
        trace.Property(item => item.TotalTokens).ToJsonProperty("totalTokens");
        trace.Property(item => item.UsageSource).ToJsonProperty("usageSource");
        trace.Property(item => item.StartedAt).ToJsonProperty("startedAt");
        trace.Property(item => item.CompletedAt).ToJsonProperty("completedAt");
        trace.Property(item => item.DurationMs).ToJsonProperty("durationMs");
        trace.Property(item => item.ImageCount).ToJsonProperty("imageCount");
        trace.Property(item => item.ImageSize).ToJsonProperty("imageSize");
        trace.Property(item => item.ImageQuality).ToJsonProperty("imageQuality");
        trace.Property(item => item.Status).ToJsonProperty("status");
    }
}