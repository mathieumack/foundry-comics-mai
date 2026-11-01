---
description: "Rules for deterministic xUnit/VSTest regression tests and choosing the correct test project."
applyTo: "src/MaiImageGenerator.Tests/**,tests/MaiImageGenerator.Tests/**"
---

# Tests

- Both existing projects use xUnit/VSTest. Use dotnet test with --filter and actual class/method names for focused runs.
- src/MaiImageGenerator.Tests covers shared coordination, retries, and image-generation leases.
- tests/MaiImageGenerator.Tests/MaiImageGenerator.Main.Tests.csproj covers API/data workflows, traces, exports, downloads, and Razor rendering.
- Use deterministic provider, HTTP, storage, and clock doubles; ordinary tests must not require Azure credentials or paid models.
- Cover happy paths and the affected failure/cancellation/ownership behavior. Do not weaken assertions to make a regression pass.
- Report skipped optional integration checks separately; EPUBCheck requires EPUBCHECK_JAR.
- Do not claim mocked rendering tests prove live Entra, SignalR, private networking, or end-to-end generation.
