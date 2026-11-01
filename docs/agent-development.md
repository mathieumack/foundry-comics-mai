# Development with GitHub Copilot agents

This repository keeps agent guidance in source control so GitHub coding sessions do not depend on a contributor's VS Code plugins, personal skills, or local Azure configuration. Copilot development agents are separate from the application's Microsoft Agent Framework runtime.

## Configuration map

| Mechanism | Responsibility |
| --- | --- |
| [Global instructions](../.github/copilot-instructions.md) | Concise repository-wide ownership, security, documentation, and validation invariants. |
| [Path-specific instructions](../.github/instructions) | Web, API, worker, Shared, infrastructure/automation, and test rules applied to matching files, regardless of selected specialist. |
| [Custom agents](../.github/agents) | Bounded specialist roles with explicit tool access. |
| [Skills](../.github/skills) | On-demand procedures reused by ordinary Copilot and specialists. |
| [Coding task template](../.github/ISSUE_TEMPLATE/coding-task.yml) | Observable outcomes, acceptance criteria, scope, dependencies, checks, documentation, and constraints. |
| [Validation workflow](../.github/workflows/validate.yml) | Credential-free build, tests, Bicep compilation, and local documentation-link checks. |
| [Copilot setup](../.github/workflows/copilot-setup-steps.yml) | Tool preparation and dependency restoration for GitHub Actions-based agent sessions. |

Keep mandatory rules in instructions, procedures in skills, and role boundaries in agent profiles. Avoid copying architecture prose into each profile.

## Choosing an agent

Use ordinary Copilot as the feature owner for cross-cutting work. Select a specialist when most of the task falls within its scope:

| Profile | Use for | Companion skills |
| --- | --- | --- |
| [Frontend](../.github/agents/frontend.agent.md) | Blazor, editor state, API clients, accessibility, rendering and download behavior. | Development and frontend verification. |
| [Backend](../.github/agents/backend.agent.md) | HTTP, persistence, queue submission, contracts, worker recovery and status. | Development; generation changes when providers are involved. |
| [Generation](../.github/agents/generation.agent.md) | Microsoft Agent Framework, prompts, structured output, provider integration, and traces. | Development and generation changes. |
| [Infrastructure](../.github/agents/infrastructure.agent.md) | Active Bicep graph, azd hooks, identity, networking, and deployment configuration. | Deployment and documentation. |
| [Documentation](../.github/agents/documentation.agent.md) | Canonical guides, diagrams, and code-to-documentation consistency. | Documentation. |

The documentation profile has read/search/edit tools but no shell. Its feature owner runs link validation. Implementation profiles have shell access for validation; this is not a security sandbox, and their tool lists are not filesystem edit boundaries.

All profiles omit a model override so the session's configured default remains in control. No profile requires a local extension or an MCP server. Add external integrations only when a task actually requires them, with minimal tools and approved access.

### Subagents

A subagent is a bounded delegation within a session, not a second GitHub issue or an automatic cross-PR coordinator. GitHub's custom-agent configuration supports an `agent` tool alias for delegation. The ordinary feature owner can consult specialists when the session exposes that capability; the specialist profiles intentionally do not enable recursive delegation.

Delegate only when context isolation is useful, such as investigating retry implications while the feature owner handles another independent part. Specify the question, allowed surfaces, expected output, and stop condition. Prefer read-only research to concurrent edits. Return findings and integration requirements to the feature owner.

Do not delegate every small task, build deep hierarchies, or have several agents edit the same contracts, service registrations, or entities. The feature owner remains responsible for complete integration, tests, and documentation.

## GitHub task workflow

1. Merge the configuration into the default branch. Refresh the GitHub agent picker and select this repository and the relevant custom agent when starting/assigning the task.
2. Use the coding task issue form. Define user-visible results, happy/failure/cancellation paths, owner isolation, exclusions, and measurable checks. Include only synthetic examples.
3. Keep a tightly coupled feature in one issue/PR even if it spans UI and backend. Split independent outcomes into separate issues, explicitly linking prerequisites.
4. Establish shared contracts before parallel implementation. Merge prerequisite changes before dependent sessions start; avoid overlapping file ownership.
5. Let the selected agent implement the outcome and regression coverage. Request a completion report listing changed behavior, checks passed/failed/skipped, documentation, and live behavior not verified.
6. Review the diff and validation evidence. Request fixes in the existing task context when practical; merge only after relevant checks and human review.

Labels do not automatically select specialists. This repository does not install automatic issue assignment, label routing, or multi-agent orchestration.

Example task:

> Preserve the selected scene when a different scene's generated image arrives. Cover partial progress while another image download is pending, preserve explicit API error handling, update the functional guide if behavior changes, and report focused rendering tests. Do not deploy or call live models.

## GitHub versus local customization

Repository profiles and skills travel with the source. User-profile custom agents, local Azure extensions, VS Code MCP settings, and personal plugins do not automatically become available on GitHub.

- Use GitHub-supported frontmatter and tool aliases. VS Code `handoffs` and `argument-hint` are not a cloud orchestration mechanism and are not used here.
- Configure any required MCP integration explicitly for GitHub; do not assume local extension tools exist there.
- Keep secrets out of issues, profiles, examples, and logs. Ordinary validation requires no Azure credentials or paid model calls.
- Live model evaluations, deployments, live data changes, and teardown require explicit approval for that operation.

## Setup and validation

### Agent environment

For sessions running on GitHub Actions, the setup workflow must be on the default branch and its job must be named `copilot-setup-steps`. It prepares .NET 10, verifies PowerShell, installs Bicep using Azure CLI, restores both test projects through the solution, and caches NuGet packages using project-file hashes. It performs no Azure login or provisioning.

The workflow uses Ubuntu's PowerShell and Azure CLI. Its manual trigger and file-specific PR/push triggers allow setup changes to be tested. Confirm the setup log succeeds before attributing build failures to the agent.

This workflow does **not** configure cloud-sandbox sessions or self-managed environments. Check the execution environment selected in GitHub. For another environment, provide equivalent .NET 10, PowerShell, Azure CLI/Bicep, and restore steps using that environment's supported setup mechanism. Do not assume GitHub Actions caches or local machine state are present.

### Required checks

The validation workflow runs for pull requests, merge groups, manual requests, and as a reusable workflow before [Azure deployment](../.github/workflows/azure-dev.yml). It has read-only repository permissions, receives no deployment secrets, and runs:

- **Build and test:** restore, Release solution build, and both xUnit/VSTest projects; TRX results are uploaded.
- **Compile Bicep:** compile the active `infra/main.bicep` graph without login or a generated checked-in ARM artifact.
- **Documentation links:** check inline local Markdown link/image targets across repository Markdown files.

Checks are not path-filtered, so required checks are not left waiting because a workflow was skipped. The deployment job depends on successful validation for both main-branch and manual deployment runs. Its existing Azure/OIDC provisioning behavior remains separate from credential-free checks.

A repository administrator must enable the three validation checks as required checks in the default branch's ruleset/protection after they have run and GitHub offers their names. Preserve existing protections, restrict direct pushes/bypasses as appropriate, and require human review. Workflow files alone do not configure repository rules. Do not require the path-filtered Copilot setup workflow for every PR. Merge-group support here does not itself enable a merge queue.

Run local equivalents from the repository root:

```powershell
dotnet build .\src\MaiImageGenerator.slnx --configuration Release
dotnet test .\src\MaiImageGenerator.slnx --configuration Release --no-build
az bicep build --file .\infra\main.bicep --stdout > $null
pwsh -File .\scripts\validate-doc-links.ps1
```

During development, use the [focused validation commands and coverage boundaries](architecture.md#development-and-validation) and the [development skill's matrix](../.github/skills/foundry-comics-development/SKILL.md#completion). Build the solution and compile the active Bicep graph for shared/deployment changes.

The link checker ignores fenced examples, external URLs, and fragments; it does not verify heading anchors, reference-style links, or remote availability. Optional EPUBCheck integration requires `EPUBCHECK_JAR`; report skipped checks honestly. No workflow proves live Entra, SignalR, private networking, quota, or generation correctness. Use approved [deployment smoke checks](deployment.md#5-verify) for those boundaries.

### Generation quality and browser evidence

Use the [generation changes skill](../.github/skills/generation-changes/SKILL.md) for deterministic provider/normalization regressions. Unit-test success is not a prose or image-quality evaluation; approved live evaluations should use fixed synthetic inputs and explicit quality criteria.

Use the [frontend verification skill](../.github/skills/frontend-verification/SKILL.md) for rendering regressions and available local browser checks. The repository has no full Azure emulator or live end-to-end harness. Report unavailable browser/authentication checks rather than inventing success.

## Measuring effectiveness

Track first-pass CI success, human review rework, and time from assignment to merge over a consistent period. Compare similar tasks and distinguish setup failures from code failures. Keep a specialist or skill when it improves those outcomes; reduce overlapping guidance or roles when routing and maintenance cost exceed the benefit. The number of agents invoked is not a success metric.

## References

- [GitHub custom-agent configuration](https://docs.github.com/en/copilot/reference/custom-agents-configuration)
- [Creating custom agents](https://docs.github.com/en/copilot/how-tos/copilot-on-github/customize-copilot/customize-cloud-agent/create-custom-agents)
- [Agent skills](https://docs.github.com/en/copilot/concepts/customizations/agent-skills)
- [GitHub Actions-based agent environment setup](https://docs.github.com/en/copilot/how-tos/copilot-on-github/customize-copilot/customize-cloud-agent/customize-the-agent-environment)
