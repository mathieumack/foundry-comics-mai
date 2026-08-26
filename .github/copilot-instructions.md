# Project Guidelines

- Keep chat responses concise: report the outcome, validation, and blockers without repeating plans or unchanged context. Put durable detail in project files.
- Write all repository documentation in English and update the relevant `docs/` page when behavior, configuration, architecture, dependencies, or deployment changes.
- Preserve component ownership: Web handles UI/authentication, API validates and queues work, the Job generates artifacts, and Shared owns cross-process contracts and data types.
- Preserve owner isolation: derive `ownerId` from authentication, keep Cosmos queries partition-scoped, and include ownership in artifact paths and messages.
- Never commit secrets, connection strings, tokens, generated `.azure` state, or tenant-specific endpoints. Prefer managed identity, RBAC, Bicep outputs, and environment variables.
- Treat `azure.yaml`, `infra/main.bicep`, and `infra/main.bicepparam` as the deployment sources of truth. Keep lifecycle hooks idempotent.
- Before completion, run the narrowest relevant checks. For shared or deployment changes, build the solution and compile the active Bicep graph.
- Use the project skills for detailed workflows instead of expanding this always-loaded file.