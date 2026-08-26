---
name: project-documentation
description: "Create or update MAI Comic Studio documentation when features, configuration, architecture, dependencies, security boundaries, models, or azd deployment behavior change. Use for README, docs, Mermaid diagrams, requirements, deployment instructions, release preparation, or public repository documentation."
---

# Project Documentation

## Canonical pages

- Root `README.md`: concise public overview, value, quick start, and links.
- `docs/architecture.md`: components, data flow, trust boundaries, models, and Mermaid diagrams.
- `docs/requirements.md`: tools, permissions, regions, versions, quota, cost, and readiness.
- `docs/deployment.md`: supported `azd` lifecycle, verification, redeployment, teardown, and troubleshooting.
- Component README files: short indexes for their directory only.

## Workflow

1. Verify behavior against code, `azure.yaml`, and the active Bicep graph before documenting it.
2. Write all repository documentation in English.
3. Update the canonical page instead of duplicating detailed prose across files.
4. Keep commands executable from the repository root unless stated otherwise.
5. Use neutral examples; never include subscription IDs, tenant IDs, client IDs, connection strings, personal paths, or deployed hostnames.
6. Update Mermaid diagrams when components, resource relationships, or request flows change.
7. Validate relative links and search for stale file names after edits.