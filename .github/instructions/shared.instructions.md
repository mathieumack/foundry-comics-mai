---
description: "Rules for shared entities, configuration, generation coordination, and cross-process queue contracts."
applyTo: "src/MaiImageGenerator.Shared/**"
---

# Shared

- Keep cross-process types here; keep UI and HTTP concerns in their owning projects.
- Update producers, consumers, schema handling, and serialization tests together. Preserve compatibility or explicitly version breaking messages.
- Preserve owner partition keys and artifact/message ownership; do not accept client ownership as authoritative.
- Follow existing Cosmos-owned entity cloning and tracking patterns when replacing nested objects.
- Preserve generation lease, concurrency, and retry-header semantics with deterministic tests.
- Build the solution, run affected tests in both suites, and compile the active Bicep graph for shared changes.
