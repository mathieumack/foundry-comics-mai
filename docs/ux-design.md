# UX redesign

## Scope and preview

### Approved palette

The approved palette is applied to both the mockup and the Blazor frontend, reusing the existing light/dark comic background images. The light palette uses pale-blue primary buttons, slate-grey labels and cool paper surfaces. The dark palette uses electric-blue primary buttons, violet secondary labels and borders, and navy surfaces. Primary-action fills are separate from link and focus colors to preserve readable contrast. Radzen controls share the same semantic theme variables.

Open [the interactive HTML prototype](ux-prototype.html) directly in a modern browser. It is a French-language UI proposal, with embedded original SVG illustrations and relative links to the two background images under the Web project's assets. Keep the repository folder structure when opening it; there are no external dependencies, network calls, or authentication. Use `?scoutTheme=light` or `?scoutTheme=dark` to select the initial theme.

The prototype covers the project library, guided creation, comic scene editing, project characters and references, board preview and simulated generation, and a separate illustrated-book workspace. It is not a replacement for the production Blazor frontend. All edits are held in memory and disappear on reload. Reference selection only retains the filename; it does not read or upload the file.

## Implementation status

The Blazor frontend now implements the responsive workshop and library, three-step creation flow, scene-focused comic editor, project character management, chapter-focused book editor, help and theme switching. Existing API routes, board history, private references, PNG export and generation operations are preserved. The prototype remains a standalone simulation, not a production client.

Drafts are not autosaved. Navigation guards protect unsaved edits, and save/generation labels reflect successful API operations. The book editor retains separate chapter text/image actions, missing-content generation, plan regeneration and explicit error feedback. See [the frontend architecture](architecture.md#frontend-workshop) for routes and persistence boundaries.

The comic detail workspace keeps its three step buttons on one row above 650px and stacks them on narrow screens. In **Scenes**, numbered selectors and the compact layout preview select the active panel; its generated image appears at full editor width above its description and opens in the full-screen viewer. Missing images retain explicit pending, generating or error feedback. Successful panel images remain accessible after a partial generation failure. **The board** shows only the assembled board, or a waiting/unavailable message with a link back to the scenes; individual panel images are not repeated there.

Validation includes the Web build, theme-script syntax and browser scenarios with mocked authentication/API responses: creation, character persistence, scene navigation guards, partial board failures and manual recovery, book editing/save failures, chapter actions, integer validation and narrow layouts. Live Entra, SignalR reconnection and Azure generation still require integration testing; no usability or full accessibility audit is claimed.

## Findings before the redesign

| Existing surface | Observation | User need | Proposed treatment |
| --- | --- | --- | --- |
| `MainLayout.razor` | The primary entry is “New board”; project navigation is separate, while the header emphasizes technical browser connectivity. | Understand where to start and how to resume work. | Make the personal workshop the entry point; foreground stories and a single creation action. Keep technical diagnostics out of the primary header. |
| `Projects.razor` | Comic and Book projects have generic thumbnails; creation exposes project type and metadata immediately. | Recognize a story visually and understand the two formats. | Illustrated cards, explicit format labels, title search and type filters. Describe the outcome of each format before asking for metadata. |
| `Home.razor` | Story scaffolding, metadata, layout, panel prompts, references and generation share a long control rail. The preview is already a useful second column. | Get a first result without filling out an intimidating form. | Idea → universe → review. Then edit one selected scene beside its board preview. Keep full manual editing; collapse advanced model settings. |
| Comic panel editing | Reordering and character assignment exist, but prompts dominate the page. | Connect each description to its visual position. | Numbered scene selectors and clickable preview cells, previous/next ordering, explicit character selection. |
| Character references | References are shared at project level, but the current UI restricts additions to the initial creation flow. | Establish recognizable characters without losing project context. | A project-level character tab. Wiring additions for existing projects is a separate Blazor implementation task. |
| Generation and export | The app already has panel progress, error details, final prompts and PNG download. | Know what is happening, what failed, and what to do next. | Persistent progress near the primary action; successful panels remain visible after a partial failure; label full-board regeneration accurately. Preserve technical prompt inspection in the production implementation. |
| `Book.razor` | Every chapter exposes summary, continuity, prose, prompt and generation actions in one long page. | Navigate a plan, focus on one chapter, distinguish text from image status. | Chapter navigator and one active editor. Separate writing and illustration actions and status. Collapse continuity fields. |
| Authentication / not found | Authentication is delegated to `RemoteAuthenticatorView`; the not-found page provides minimal guidance. | Recover without losing orientation. | Retain the Entra flow. Later add a consistent signed-out welcome screen and a not-found action back to the workshop; these are not simulated authentication features. |

These are code-based observations, not findings from user interviews, analytics, or a live usability study.

## Design principles

- **Story first:** use plain creative language rather than implementation vocabulary such as scaffolding.
- **One next action:** guide initial creation, then let experienced users adjust every scene.
- **Make the artifact tangible:** original editorial-style illustrations, comic backgrounds, numbered pages, theme-specific blue accents and generous whitespace. Violet secondary accents connect the dark UI to its background artwork.
- **Progressive disclosure:** show creative decisions first; reveal prompts and technical controls on demand.
- **Separate formats, consistent shell:** a comic has boards and panels; a book has a plan, chapters, prose and one illustration per chapter.
- **Trust over magic:** distinguish simulation, saved state, generation, partial failure and export. Never imply that an unimplemented action succeeded.
- **Accessible by default:** native buttons and dialogs, visible focus, dialog Escape handling, labelled inputs, live feedback, text accompanying statuses, reduced-motion support and responsive navigation. A full accessibility audit is still required.

## Try these prototype paths

1. Filter the story cards, search for a title, and reset an empty search.
2. Create a story; submit an empty idea to see validation, pick an inspiration, choose a format and style, then review.
3. In a comic, select a preview cell, edit its description, reorder it and assign characters.
4. Add a character and select a local PNG/JPEG reference. Only the filename is retained.
5. Simulate board generation. Leave for the library and reopen the project while it runs.
6. Enable the case-2 incident scenario, generate, inspect the partial result, then relaunch without the incident.
7. Export a completed example. The downloaded SVG contains the selected layout and an explicit demonstration label. Production PNG export remains unchanged.
8. Open the book, select a chapter, edit its summary, generate example prose and display the example illustration independently.
9. Switch between light and dark themes; try a narrow/mobile viewport and keyboard-only navigation.

## Blazor and Radzen implementation mapping

| UI responsibility | Suggested building blocks | Integration notes |
| --- | --- | --- |
| Shared shell | `RadzenLayout`, `RadzenSidebar`, `RadzenPanelMenu`, `RadzenButton` | Replace layout and navigation together; retain authorized-route boundaries and Entra/MSAL. |
| Story library | `RadzenCard`, `RadzenTextBox`, filter buttons, CSS Grid | Keep current type-specific routes. Reuse owner-scoped list responses; a true cover thumbnail may require a separately reviewed API response addition. |
| Guided creation | `RadzenDialog`, `RadzenSteps`, `RadzenTemplateForm`, validators | Comic: reuse the scaffold operation and editable draft. Book: create the project then request its asynchronous plan. Preserve drafts when navigating between steps or when an API call fails. |
| Scene workspace | `RadzenTabs`, `RadzenTextArea`, multi-select `RadzenDropDown` | Bind to the existing panel drafts and reorder operations. The preview follows the selected slot definition, not an independent layout model. |
| Layout choices | Existing `BoardTemplateDefinitions` | Standard has 6 slots; Storyline 3; Dynamic1 and Dynamic2 5, with the full-width slot in the middle or first row respectively. The prototype matches those counts and placements. |
| Characters | `RadzenCard`, `RadzenTextBox`, existing `InputFile` flow | Reuse the authenticated project-character/reference endpoints. Keep owner isolation and private artifact access unchanged. |
| Progress and errors | `RadzenProgressBar`, `RadzenAlert`, `NotificationService` | Reuse board SignalR notifications and durable status refresh. Preserve bounded refresh retries, panel-level safety failures, effective-prompt inspection, and PNG streaming. Do not infer accurate time estimates from the prototype timer. |
| Book workspace | Chapter navigation, `RadzenTextArea`, `RadzenAccordion`, separate buttons | Preserve plan polling, explicit save, regeneration warnings, independent prose/image errors, and generate-remaining coordination. Do not promise background chapter generation: existing chapter operations are API-coordinated, unlike durable plan and board jobs. |

Reuse the installed Radzen package; this proposal adds no dependencies. Native HTML controls in the mockup illustrate interaction contracts, not a requirement to replace Radzen.

## Deliberate prototype limits

- Sample scenes, chapter plans, prose and illustrations are fixed local examples; they do not interpret the entered idea.
- One active board per comic is simulated. Creating another explicitly confirms replacement. Production must retain all existing board routes and history.
- Style examples are illustrative, not guaranteed model outputs.
- The comic incident scenario is synthetic. Retrying regenerates the entire board, not a single panel; per-panel retry would require separate backend work.
- The book prototype omits plan regeneration, the complete set of independent error states, and “generate remaining chapters.” These existing capabilities must not be removed when implementing the redesign.
- Session memory is not autosave. Production must show actual saved/unsaved state and guard against losing edits; it must not display a saved badge before persistence succeeds.
- No pricing, generation-time promises, public sharing, collaborative editing, PDF export or additional production formats are introduced.

## Suggested delivery order and acceptance checks

1. Introduce shell, library, responsive cards and accessible empty/loading/error states without changing API contracts.
2. Add guided creation and contextual scene editing; preserve current templates, manual inputs, rewrite switch, project associations and reference uploads.
3. Add the chapter navigator while preserving every existing book operation and recovery path.
4. Validate keyboard and screen-reader flows, mobile layouts, real generation failures, reconnects, save failures and reopening durable jobs.

Measure success with representative first-time and returning users: can they distinguish comics from books, reach an editable storyboard without assistance, find an existing story, and recover from a failed image? Record completion rate, time on task and points of confusion before setting improvement targets. No usability improvements are claimed as measured by this mockup.
