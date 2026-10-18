# Documentation

- [Functional guide](functional-guide.md): workshop navigation, comic and book journeys, board/image processing flows, limits, save/resume behavior, and failures.
- [Architecture](architecture.md): application components, Azure resources, data flow, identity, and networking.
- [Requirements](requirements.md): local tools, Azure permissions, model availability, quota, and operational considerations.
- [Deployment](deployment.md): provision, deploy, verify, update, troubleshoot, and remove an environment with `azd`.
- [Responsible AI and transparency FAQ](responsible-ai.md): educational purpose, model limitations, human review, data handling, copyright, and MIT licensing.
- [UX redesign proposal](ux-design.md): frontend findings, proposed user journeys, Radzen mapping, and a standalone [interactive HTML prototype](ux-prototype.html).

## Diagrams

- [Application overview (SVG)](images/solution-architecture.svg) and its [editable Excalidraw source](images/solution-architecture.excalidraw).
- [Technical topology and sequence diagrams](architecture.md#azure-topology) in Mermaid.
- [Functional board and image flows](functional-guide.md#board-processing-flow) in Mermaid.

The SVG and Excalidraw source represent the same application-level view; update both when that view changes. The detailed Mermaid topology additionally covers infrastructure and network boundaries.

The root [README](../README.md) is the project overview. Keep detailed operational guidance in this directory so the landing page remains concise.