---
description: "Rules for the active Azure deployment graph, azd hooks, and credential-free automation."
applyTo: "infra/**,azure.yaml,scripts/*.ps1,.github/workflows/**"
---

# Infrastructure and automation

- Use the azd-deployment skill for deployment changes; edit the active graph and preserve its parameter mappings.
- Keep lifecycle hooks idempotent and preserve managed identity, RBAC, private endpoints/DNS, and conditional Entra management.
- PR validation and Copilot setup must not authenticate to Azure, provision resources, or invoke live models.
- Keep deployment permissions out of validation jobs; use minimal workflow permissions and existing OIDC deployment boundaries.
- Compile infra/main.bicep without producing a checked-in ARM artifact; build the solution for deployment changes.
- Parse edited PowerShell scripts without running deployment hooks. Compilation is not a live quota, networking, or permission check.
