---
description: "Rules for Blazor components, authentication UI, editor state, and API clients."
applyTo: "src/MaiImageGenerator.Web/**"
---

# Web

- Web owns UI/authentication and display state, not trusted authorization or persistence rules.
- Use the existing authenticated API client; do not persist tokens, project content, or artifacts in browser storage as a shortcut.
- Preserve independent loading/error states, partial image progress, selected scene/chapter, cancellation, and unsaved-change guards.
- Reuse Radzen and theme tokens; keep keyboard access, focus, labels, and light/dark contrast usable.
- Surface API failures explicitly; do not replace failed live requests with mock success.
- Use the frontend-verification skill and add rendering/download regressions to the existing main test project.
