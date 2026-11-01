---
description: "Rules for board and book-plan workers, duplicate delivery, lock renewal, retries, and durable generation."
applyTo: "src/MaiImageGenerator.Jobs.BoardGeneration/**,src/MaiImageGenerator.Jobs.BookPlanGeneration/**"
---

# Workers

- Keep board generation and book planning in their respective workers; preserve ownerId in messages, data access, and artifact paths.
- Treat queue delivery as repeatable: reuse completed work and preserve recoverable durable transitions.
- Preserve PeekLock renewal, cancellation, retry bounds, dead-letter behavior, and configured image concurrency.
- Persist progress before publishing notifications; notifications are not the source of truth.
- Do not replace managed-identity Service Bus access with keys.
- Use the generation-changes skill for model/prompt work. Verify contract changes against API producers and older messages.
