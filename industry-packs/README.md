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
- `INDUSTRY_CHARTER.md`: customer, outcome, boundaries and success metrics.
- `workflows/`: explicit task graphs with inspectable intermediate artifacts.
- `knowledge/`: sourced facts separated from assumptions and customer data.
- `evaluations/`: repeatable cases and acceptance criteria.
- `delivery-templates/`: the result shape the customer actually receives.

