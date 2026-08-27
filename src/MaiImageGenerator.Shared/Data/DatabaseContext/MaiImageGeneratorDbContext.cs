using MaiImageGenerator.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MaiImageGenerator.Shared.Data.DatabaseContext;

public sealed class MaiImageGeneratorDbContext(
    DbContextOptions<MaiImageGeneratorDbContext> options) : DbContext(options)
{
    public DbSet<GenerationDocument> Generations => Set<GenerationDocument>();
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
        generation.Property(item => item.SchemaVersion).ToJsonProperty("schemaVersion");
        generation.Property(item => item.Title).ToJsonProperty("title");
        generation.Property(item => item.Description).ToJsonProperty("description");
        generation.Property(item => item.Context).ToJsonProperty("context");
        generation.Property(item => item.RewritePrompt).ToJsonProperty("rewritePrompt");
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
        });
        generation.OwnsMany(item => item.Characters, character =>
        {
            character.ToJsonProperty("characters");
            character.OwnsMany(item => item.References, reference =>
            {
                reference.ToJsonProperty("references");
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
}