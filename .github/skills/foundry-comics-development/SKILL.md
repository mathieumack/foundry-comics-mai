---
name: foundry-comics-development
description: "Develop or review MAI Comic Studio features across Blazor, the .NET API, the board-generation job, Cosmos DB, Storage, queue contracts, prompts, and image generation. Use for application code, generation workflow, authentication, references, panels, boards, or shared contract changes."
---

# Foundry Comics Development

## Start here

1. Read [`docs/architecture.md`](../../../docs/architecture.md).
2. Locate the owning component before editing:
   - `MaiImageGenerator.Web`: browser UI, MSAL, API client, and display state.
   - `MaiImageGenerator.Api`: authenticated HTTP boundary, validation, persistence, uploads, and queue submission.
   - `MaiImageGenerator.Jobs.BoardGeneration`: prompt rewriting, image generation, composition, retries, and durable status.
   - `MaiImageGenerator.Shared`: Cosmos entities, configuration, and versioned queue contracts.
3. Follow an existing neighboring service, controller, page, or contract pattern.

## Invariants

- Derive ownership from the authenticated principal; never trust an owner ID supplied by a client.
- Query Cosmos with the `/ownerId` partition key and keep blob paths owner-scoped.
- Treat queue messages as cross-version contracts. Update schema handling explicitly when fields change.
- Keep reference images and generated artifacts behind the authenticated API.
- Use managed identity and `DefaultAzureCredential`; do not introduce service keys or secrets.
- Keep API and worker status transitions recoverable and idempotent.

## Completion

1. Update architecture or deployment documentation when contracts, configuration, dependencies, or flows change.
2. Run the narrowest available validation, then `dotnet build src/MaiImageGenerator.slnx` for cross-project changes.
3. Report missing automated coverage; the repository does not currently contain a test project.