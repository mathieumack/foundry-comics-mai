# Scripts

| Script | Purpose |
| --- | --- |
| `set-caphost-idempotency-flag.ps1` | Runs before provisioning and detects whether immutable Foundry capability-host resources already exist. |
| `prepare-frontend.ps1` | Builds the production Blazor configuration from `azd` outputs before packaging the web service. |
| `add-copyright-headers.ps1` | Maintainer utility for applying repository copyright headers. |
| `validate-doc-links.ps1` | Credential-free check of inline local Markdown link/image targets in tracked and unignored new Markdown files. |
| `comic-baseline.ps1` | Offline initialization, evidence validation, and stage/usage summaries for the 20 fixed comic evaluation briefs; never calls models. |

The first two scripts are invoked by `azure.yaml`; they are not independent deployment entry points. Follow [the deployment guide](../docs/deployment.md) for supported commands and troubleshooting.

Run `pwsh -File ./scripts/validate-doc-links.ps1` from the repository root, or pass `-Paths` to check selected Markdown files. The check ignores fenced examples, external URLs, and fragments; it does not validate heading anchors, reference-style links, or remote availability. See [agent development](../docs/agent-development.md) for GitHub validation and agent setup.

See the [editorial and reliability baseline procedure](../docs/functional-guide.md#editorial-and-reliability-baseline-e01) for fixed inputs, rubric anchors, private evidence collection, unknown metrics, and human evaluation gates.