---
description: "Rules for authenticated ASP.NET endpoints, persistence, uploads, and queue submission."
applyTo: "src/MaiImageGenerator.Api/**"
---

# API

- Derive ownerId from authentication; validate resource ownership and partition all Cosmos access by owner.
- Validate requests at the HTTP boundary and preserve existing status/error conventions and cancellation propagation.
- Keep artifacts private and verify owner-scoped blob prefixes before reading or deleting content.
- API owns validation, persistence, queue submission, and foreground chapter prose/images; do not move queued board/book-plan work into request handlers.
- Preserve recoverable status when enqueueing or persistence fails; never report success for partial failure.
- Test endpoint/data workflows in the existing main test project. Contract changes must cover worker consumers too.
