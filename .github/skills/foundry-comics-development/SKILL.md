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
   - `MaiImageGenerator.Jobs.BookPlanGeneration`: queued book planning, response normalization, and durable chapter/status persistence.
   - `MaiImageGenerator.Shared`: Cosmos entities, configuration, and versioned queue contracts.
3. Follow an existing neighboring service, controller, page, or contract pattern.
4. Only boards and book plans run in workers. Chapter prose and illustrations are foreground API operations.

## Invariants

- Derive ownership from the authenticated principal; never trust an owner ID supplied by a client.
- Query Cosmos with the `/ownerId` partition key and keep blob paths owner-scoped.
- Treat queue messages as cross-version contracts. Update schema handling explicitly when fields change.
- Keep reference images and generated artifacts behind the authenticated API.
- Use managed identity and `DefaultAzureCredential`; do not introduce service keys or secrets.
- Keep API and worker status transitions recoverable and idempotent.

## Completion

1. Update architecture or deployment documentation when contracts, configuration, dependencies, or flows change.
2. Select validation from the matrix below. Use the existing [commands and coverage boundaries](../../../docs/architecture.md#development-and-validation).
3. Run `dotnet build src/MaiImageGenerator.slnx` for cross-project changes; also compile the active Bicep graph for shared or deployment changes.
4. Report exactly which checks passed, failed, or were not run. A build does not prove live Azure behavior.

| Change | Focused validation |
| --- | --- |
| Blazor rendering, progress, downloads | Relevant tests in `tests/MaiImageGenerator.Tests/MaiImageGenerator.Main.Tests.csproj`; use the frontend-verification skill. |
| API, persistence, traces, exports | Relevant tests in `tests/MaiImageGenerator.Tests/MaiImageGenerator.Main.Tests.csproj`. |
| Shared coordination, retry headers, leases | Relevant tests in `src/MaiImageGenerator.Tests/MaiImageGenerator.Tests.csproj`. |
| Prompts, provider integration, normalization | Use the generation-changes skill; exercise deterministic failures without live model calls. |
| Cross-process messages or shared types | Build the solution, test affected producers/consumers, and compile `infra/main.bicep`. |
| Infrastructure or deployment hooks | Use the azd-deployment skill; distinguish compilation from authenticated preflight. |

Both test projects use xUnit/VSTest. Choose existing class or method names for `--filter`; do not assume similarly named project files are interchangeable. Run the full solution tests when a change spans both suites.