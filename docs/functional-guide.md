# Functional guide

## Purpose and scope

MAI Comic Studio is a training application for discovering Azure services and MAI models through comic and illustrated-book creation. It is not a production publishing tool. Use synthetic, non-sensitive stories and reference images you are authorized to process. Read the [responsible AI FAQ](responsible-ai.md) before a workshop.

The interface currently uses French labels. This guide uses English descriptions of the implemented actions.

## Navigate the workshop

| Entry point | User experience |
| --- | --- |
| `/` | Authenticated creative workshop. |
| `/projects` | Private library of saved comic and book projects. |
| `/new` | Three-step story wizard: describe, choose a format, and prepare the project. |
| `/projects/{projectId}` | Comic workspace with shared characters and board versions. |
| `/projects/{projectId}/book` | Book workspace with a chapter navigator and active chapter editor. |
| `/boards/new` | Free-form comic generation outside the guided wizard. |

Sign in with Microsoft Entra ID before using the workshop. Project metadata, reference images, generated panels, boards, and book illustrations are accessed through the authenticated API. There is no public artifact-sharing workflow.

Light/dark themes follow the browser preference initially; an explicit selection is stored locally. Library artwork indicates the project format and is not an AI-generated project cover.

## Functional overview

![Functional workflow: private story library, comic scenes with per-image updates, and book planning followed by explicit chapter actions](images/functional-workflow.svg)

The [editable Excalidraw source](images/functional-workflow.excalidraw) accompanies this overview. Detailed processing flows below distinguish what continues in the background from actions that require keeping the book editor open.

## Create a comic

1. Start a new story, enter the idea, and select the comic format.
2. Let the wizard prepare a title, context, style, and scene prompts, then review and edit them.
3. Open the created project's board editor. The scaffold is an in-memory draft, not a saved board: submit generation to persist it.
4. Add named project characters and optionally upload PNG/JPEG reference images. Select the characters appearing in each scene.
5. Choose a layout and edit or reorder scene prompts. Changing the layout rebuilds the scene slots; review the editor before submitting.
6. Choose whether to rewrite prompts, then generate a new board version.
7. In **Scenes** (`1. Les scènes`), follow per-panel progress: the selected illustration and layout thumbnails update as each image becomes available, without waiting for the composed board. Changing the selected scene remains your choice.
8. Inspect the effective prompts and any panel errors. **The board** (`3. La planche`) shows the assembled PNG after composition; download available panel PNGs and the composed board PNG.

Character references guide identity but do not guarantee visual consistency. The worker uses the project's character/reference metadata when it processes the board; avoid changing that metadata during generation.

### Layouts and limits

| Layout | Panels | Arrangement |
| --- | ---: | --- |
| Standard | 6 | Three rows of two panels. |
| Storyline | 3 | One row of three panels. |
| Dynamic 1 | 5 | Two panels, one full-width panel, then two panels. |
| Dynamic 2 | 5 | One full-width panel, then two rows of two panels. |

The API supports 1 to 8 non-empty panel prompts when no explicit template is supplied; an explicit template requires its exact panel count. The workshop uses the selected template's slots. Scaffold descriptions accept 1 to 10,000 characters. Reference uploads accept PNG/JPEG files of up to 10 MiB; the HTTP request-size limit also includes multipart overhead.

### Board processing flow

```mermaid
flowchart TD
    Idea[Describe and review the story] --> Edit[Edit layout, panels and characters]
    Edit --> Submit[Submit a new board version]
    Submit --> Validate{API validation succeeds?}
    Validate -->|No| Correct[Show error and correct inputs]
    Correct --> Edit
    Validate -->|Yes| Queue[Save Pending board and enqueue work]
    Queue --> Worker[Background job starts Processing]
    Worker --> Prepare[Prepare effective prompts and character references]
    Prepare --> Panel[Generate independent panels within the concurrency limit]
    Panel --> Result{Panel outcome}
    Result -->|Generated| Save[Save PNG and panel status]
    Result -->|Transient provider failure| Retry{Image retries remaining?}
    Retry -->|Yes| Wait[Wait for provider Retry-After or bounded backoff]
    Wait --> Panel
    Retry -->|No| Placeholder[Record panel error and placeholder]
    Result -->|Permanent failure or safety rejection| Placeholder
    Save --> Notify[Notify the owning user's workshop]
    Placeholder --> Notify
    Notify --> Scenes[Refresh saved status and render each available scene and thumbnail]
    Scenes -.-> Poll[While active, refresh status every 5 seconds even without push]
    Notify --> Settled{All panel tasks settled?}
    Settled -->|No| Other[Other panel tasks continue]
    Settled -->|Yes| Compose[Compose and save board PNG]
    Compose --> Completed[Mark Completed and retrieve artifacts through API]
    Poll -.-> Scenes
    Worker -->|Uncaught job or persistence failure| Failed[Save board failure and allow queue redelivery]
```

The board can be `Completed` while individual panels are `Failed`, including content-safety rejections or exhausted image-provider retries. Those slots contain placeholders; completion does not mean every scene produced an image. An isolated panel failure does not cancel other panels.

Board generation continues if the page closes. Reopen the project or generation to retrieve durable status. Progress events help refresh the editor but are not the authoritative record; stored API status is. A notification can arrive before the latest image/status is visible, so the frontend performs bounded refresh retries and explicitly renders each downloaded image. While a board is pending or processing, a five-second status refresh also recovers missed events and detects final composition. Normal progress does not require a manual refresh.

Uncaught job failures can trigger automatic queue redelivery. Completed panels with existing blobs are reused when an unfinished board resumes; a terminal `Completed` board is not automatically replayed even if it contains failed panels. To change a prompt or retry a failed scene as a user, edit the board and submit a new version; there is no dedicated single-panel retry endpoint.

## Export as an EPUB eBook

Use **Export as EPUB** (`Exporter en EPUB`) in either project workspace. This is an on-demand frontend action: the signed-in user downloads their eBook directly through the authenticated API, without a GitHub workflow or export automation. Finish generation and save edits first; the action is disabled while content is incomplete or there are unsaved changes. Keep the page open until the browser starts downloading `<project-title>.epub`. The filename is sanitized automatically.

- **Illustrated books:** reflowable EPUB 3.3 with a title-page cover, project title and identifier, table of contents, chapter prose in saved chapter-number order, and available PNG illustrations. The first available illustration also appears on the cover. Missing optional images do not block export, including books with no illustrations. Every chapter must have saved prose. Plain text, paragraph breaks, and line breaks are preserved; typed HTML is text, not markup.
- **Comics:** fixed-layout EPUB 3.3 with one page per completed composed board, oldest creation time first (identifier breaks ties). All completed board versions in the project are included; terminal failed/canceled attempts are skipped so a successful regeneration restores export. There is no separate curated page-order field. Each page uses the actual PNG dimensions and preserves its aspect ratio and panel layout. Any placeholders already in the composed board remain in the export.
- Both formats include a navigation document and modification timestamp. Language is `und` (undetermined) because projects do not currently store a language. No author name is inferred from the user's account.
- Private images are embedded, not linked to authenticated API URLs. The download is owner-authorized and not cached or saved as another project artifact. Once downloaded, the EPUB is an ordinary unencrypted file: keep it private or share it only deliberately.

An empty project is rejected with a message explaining what to generate. Active comic generation must finish before export, and at least one board must be completed. A missing composed PNG requires regenerating that board; a transient storage error can be retried. Missing book illustrations are simply omitted. If a board completed in another tab, reopen the workspace to refresh its state before exporting.

### Reader compatibility and validation

Fixed-layout support varies. Readers that only reflow text, including some browser EPUB extensions and older e-ink software, may ignore viewport or rendition metadata. Use a fixed-layout-capable reader for comics; text selection, font resizing, and accessible text extraction are not available inside rasterized comic boards. Book text can reflow, but illustration placement and pagination remain reader-dependent. Kindle does not natively open EPUB files; conversion/send-to-device behavior is outside this application's guarantees.

The following is a manual smoke-test checklist, **not a claim that these readers have been tested in CI**. Desktop readers are not available in the automated test environment.

| Reader | Book checks | Comic checks | Validation status |
| --- | --- | --- | --- |
| Apple Books (macOS/iOS) | Open, cover, contents, resize prose, illustrations, offline reading | Fixed-layout viewport, page order, portrait/landscape fit | Manual verification pending |
| Calibre e-book viewer | Open, contents links, chapters without images | Check fixed-layout behavior in the installed version; do not assume conversion preserves it | Manual verification pending |
| Thorium Reader | Open, contents, prose and images | Fixed-layout pages, navigation, aspect ratio | Manual verification pending |
| A fixed-layout-capable reader (Apple Books or Thorium) | — | Compare every exported page with its source PNG and test mixed dimensions | Manual verification pending |

Automated tests check ZIP/container structure, XML encoding, metadata, chapter/board order, embedded assets, owner isolation, cancellation, and both layouts. Optional local EPUBCheck 5.3.0 tests validate representative book and comic files without errors or warnings; they do not export user projects. No EPUB runtime library is added: the API uses .NET ZIP/XML APIs and reads PNG headers without decoding images.

To run the optional validator tests locally, install Java 17 or later, download and extract the [EPUBCheck 5.3.0 release](https://github.com/w3c/epubcheck/releases/tag/v5.3.0), then run from the repository root:

```bash
export EPUBCHECK_JAR=/absolute/path/to/epubcheck-5.3.0/epubcheck.jar
dotnet test tests/MaiImageGenerator.Tests/MaiImageGenerator.Main.Tests.csproj --filter FullyQualifiedName~Epub
```

Without `EPUBCHECK_JAR`, only the EPUBCheck integration cases are skipped; package/unit tests still run.

## How a panel image is produced

```mermaid
flowchart TD
    Input[Scene prompt and project context] --> Rewrite{Prompt rewriting enabled?}
    Rewrite -->|Yes| Thinking[MAI-Thinking-1 prepares the effective prompt]
    Rewrite -->|No| Original[Keep the original prompt]
    Thinking --> Characters[Resolve selected project characters]
    Original --> Characters
    Characters --> References{Reference images available?}
    References -->|Yes| Sheet[Build a reference sheet and identity guidance]
    References -->|No| TextImage[Text-to-image request]
    Sheet --> EditImage[Reference-guided image edit request]
    TextImage --> MAI[MAI-Image-2.5-Pro]
    EditImage --> MAI
    MAI --> Outcome{Outcome}
    Outcome -->|Image| Store[Store private panel PNG and Completed status]
    Outcome -->|Transient failure| Retry{Retries remaining?}
    Retry -->|Yes| Delay[Honor Retry-After; 429 without a delay waits at least 60 seconds]
    Delay --> MAI
    Retry -->|No| Error[Store Failed status and visible error]
    Outcome -->|Permanent failure or content safety rejection| Error
    Store --> Progress[Notify owner and display this image in Scenes]
    Store --> Board[Crop to the board slot without distortion]
    Error --> Blank[Use a placeholder in the board]
```

Regular panels request 1024 x 1024 pixels. Full-width slots request 1365 x 768 pixels. Board composition crops to fit the selected slot; preview composition and the generated source image are not identical dimensions.

## Create an illustrated book

1. Enter a source idea and select the book format in the wizard.
2. Create the project and request a plan of 1 to 30 chapters. Planning is queued and can finish while the page is closed.
3. Review chapter titles, summaries, character events, and continuity notes. Save edits explicitly.
4. Select a chapter. Generate its prose, edit it if needed, and save.
5. Enter an illustration prompt or allow the API to construct one from the chapter metadata. Generate one image for that chapter.
6. Repeat chapter by chapter, or use the generate-missing action to coordinate unfinished prose and image stages from the browser.

The prose request includes the current global plan and previously generated chapter prose. This provides context, not a guarantee of consistency. Chapter illustrations currently use text prompts only; they do not consume the comic character reference sheets or automatically illustrate the complete prose.

### Book and chapter image flow

```mermaid
flowchart TD
    Idea[Create Book project from source idea] --> Plan[Queue chapter planning]
    Plan --> Worker[Dedicated book-plan job]
    Worker --> Review[Poll saved plan, review and edit chapters]
    Review --> Save[Save current plan and prose edits]
    Save --> Choose{Select a chapter action}
    Choose -->|Write prose| Text[API calls MAI-Thinking-1 with plan and prior prose]
    Choose -->|Illustrate| Prompt[Use saved image prompt or construct chapter prompt]
    Prompt --> Image[API calls MAI-Image-2.5-Pro without references]
    Text --> ProseState[Save prose status, content or error]
    Image --> ImageState[Save image status and private PNG, or error]
    ProseState --> Continue[Review and independently retry unfinished stages]
    ImageState --> Continue
    Continue --> Choose
```

### Save, resume, and regenerate

- **Plan generation:** durable, queued, and polled. A failed queue submission can be retried without creating another project.
- **Plan recreation:** replaces the plan fields. The worker preserves prior chapter IDs, prose, and image work by chapter position where positions still exist; a shorter plan removes later chapters from the current plan. Review preserved work for consistency with the new plan.
- **Prose/image generation:** foreground API calls. Keep the page open until the request completes; generate-missing is not a durable whole-book job.
- **Independent results:** prose and image each have their own status and error. An image failure does not invalidate saved prose, and vice versa.
- **Image regeneration:** overwrites that chapter's stable image path. Previous chapter-image versions are not retained by the application.
- **Unsaved edits:** navigation guards warn about leaving modified content. Unsaved browser edits are not a durable checkpoint.
- **Reopening:** restores saved content and stage statuses. Interrupted requests may require an explicit retry; there is no automatic whole-book process-loss recovery.

## Interpret failures and outputs

| Situation | Expected behavior or action |
| --- | --- |
| Invalid title, panel count, chapter count, or reference | Correct the input before resubmitting. |
| Board Pending for a long time | Work may be queued or waiting for a retry; use the deployment checks to inspect job executions. |
| Provider HTTP `429` | Image retries wait for the provider's requested delay; this is not necessarily a stuck generation. Exhausted retries leave an explicit failed panel. |
| Blocked comic panel | Read the panel error, revise the request appropriately, and create a new version; do not attempt to bypass safety controls. |
| Board technical failure | An isolated image failure can leave a placeholder in a completed board. Uncaught job failures record a board failure and can trigger queue retries that reuse completed panels. |
| Live notifications unavailable | Active generation status refreshes every five seconds. If progress still appears stale, check API connectivity; operators can inspect the Service Bus notification bindings. |
| Book plan failure | Retry planning. The invalid model response does not replace the saved chapters. |
| Chapter prose/image failure | Retry only the affected stage after reviewing its error. |
| Another user's project/artifact identifier | It must not grant access; the API resolves records within the authenticated owner's partition. |

There is no implemented PDF/EPUB whole-book export, collaborative editing, publication workflow, or production support commitment. The persisted status type includes `Canceled`, but there is no user-facing cancellation endpoint.

For operational diagnosis, see [deployment](deployment.md). For storage, queues, endpoints, and trust boundaries, see [architecture](architecture.md).
