# NOVA Industry Packs

Industry Packs specialize NOVA AgentOS without forking or exposing the AgentOS core.
Each pack owns its domain vocabulary, task graph, evidence rules, delivery templates,
evaluations and optional MCP/A2A adapters. It must not bypass workspace containment,
permission approval, budget governance or Proof-of-Done.

An incubating pack lives in this directory until its contract and first paid workflow
are stable. It can then move to an independent private repository and consume NOVA
through a versioned SDK boundary.

## Minimum pack contract

- `nova.industry.json`: identity, compatibility and declared capabilities.
- `agent-card.json`: typed discovery and delegation metadata for Agent Fabric.
- `certification.json`: persisted NOVA Creation Standard validation result.
- `INDUSTRY_CHARTER.md`: customer, outcome, boundaries and success metrics.
- `workflows/`: explicit task graphs with inspectable intermediate artifacts.
- `knowledge/`: sourced facts separated from assumptions and customer data.
- `evaluations/`: repeatable cases and acceptance criteria.
- `delivery-templates/`: the result shape the customer actually receives.

## Build another industry

Copy `_template`, rename `nova.industry.example.json` to `nova.industry.json`,
and follow [the Agent Pack SDK](../AGENT-PACK-SDK.md). Operators can then import
the folder from NOVA's **Extension Dock → Agents** screen. The schema is
`nova.industry.schema.json`; import remains disabled until the user explicitly
reviews and enables the pack.

The Electron **Agent Workshop** can generate a safe starting pack across ten
scenario profiles. It standardizes reliability contracts while preserving
autonomy, lifecycle, collaboration, delivery and decision-style diversity.
