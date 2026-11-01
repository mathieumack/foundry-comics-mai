---
name: frontend-verification
description: "Verify Blazor UI changes, Razor rendering, partial generation progress, authenticated downloads, accessibility, loading and error states. Use for frontend regressions, scene/chapter editors, keyboard checks, and local browser verification."
---

# Frontend verification

## Procedure

1. Read the [implemented frontend behavior](../../../docs/architecture.md#frontend-workshop), not only the UX proposal.
2. Identify affected routes/components and API responses. Define visible acceptance criteria before changing markup/state.
3. Extend the existing main test project using neighboring rendering or download tests. Use deterministic HTTP doubles; do not embed fallback mock data in the application.
4. Cover the relevant states: initial loading, success, empty data, partial progress, request failure, retry, and cancellation/navigation. Verify selected scenes/chapters and unsaved edits remain intact.
5. For progress changes, assert an available image renders without waiting for another pending image or final composition. For downloads, preserve authenticated requests, errors, and object-URL cleanup.
6. Run focused xUnit tests in `tests/MaiImageGenerator.Tests/MaiImageGenerator.Main.Tests.csproj` using actual class/method names. Build Web, or the solution for cross-project changes.
7. Where a runnable local page is available, use browser tools available in the current session to check keyboard navigation, focus, accessible names, narrow layouts, and light/dark themes. Use synthetic data only in the test harness.
8. Stop temporary servers after verification. Do not modify real projects or use deployed authentication without approval.
9. Update the functional guide for changed user behavior; report tests and browser evidence separately.

## Boundaries

There is no complete local Azure emulator or live end-to-end test setup. Do not invent a successful browser run when authentication/backend dependencies prevent it. Mocked Razor tests do not verify live Entra, SignalR delivery, private networking, or generation. Report those gaps and use approved [deployment smoke checks](../../../docs/deployment.md#5-verify) only when explicitly authorized.
