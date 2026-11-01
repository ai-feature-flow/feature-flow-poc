---
name: verification
description: How requirements, acceptance criteria, verification methods, evidence claims and test results connect, with the exact document formats. Read before writing or checking any of them.
---

# Verification

## The chain

```text
requirement        requirements       observable behaviour; links up with supports
  └─ criterion     delivery_plan      an observable condition; supports requirements
      └─ method    delivery_plan      the repository and test tier that will prove it
          └─ claim evidence_claims    the exact tests the implementer says prove it
              └─ result  test_results the engine's own observation: passed or not
```

The engine checks structure, links, ownership and whether claimed tests passed.
Whether the requirements are the right ones, and whether a test's assertions
really show the behaviour, is judgement: a reviewer's or a person's.

## requirements

```yaml
schema: 1
scope: { kind: ticket, id: CLAIMS-1 }     # project | epic | ticket
requirements:
  - id: VR-001
    title: Reject expired claims
    behaviour: Submitting a claim after its deadline returns claim_expired.
    supports:
      - { artifact: requirements, requirement: VR-EPIC-03 }   # artifact: a consumed artifact's name
```

- A requirement states behaviour. It names no test, tier or design.
- It `supports` requirements above it, or gives a `supports_reason` (enabling
  work). Not both.
- Only a project-scope document names an `owner` per item: an epic, or the
  project itself.

## delivery_plan

```yaml
schema: 1
scope: { kind: ticket, id: CLAIMS-1 }
acceptance_criteria:
  - id: AC-001
    title: Expired claims are rejected
    behaviour: The API returns claim_expired and the stored total is unchanged.
    supports:
      - { artifact: requirements, requirement: VR-001 }
    verification:
      - id: component
        kind: parsed_test
        repository: claims-api
        tier: component
        intent: Exercise the endpoint against the real database.
        allowed_test_changes: [new, reused]
        semantic_review: required
```

- Every method is required. A method is named `<criterion>/<method>`.
- `tier` is a tier name or tag from the repository manifest. Check the baseline
  results: a tier that runs but produces no parsed results cannot prove anything.
- Only an epic-scope document names an `owner` per criterion (a ticket, or the
  epic). An owned criterion has no methods: its owner adds them.

## Ownership across levels

A parent's planner gives each item exactly one owner. A child copies every item
it owns, with `id` and `supports` unchanged and no `owner`, and holds none that
another owns. An epic copies requirements word for word; a ticket may sharpen a
criterion's wording but not its meaning. A child that owns nothing cannot run.

## evidence_claims

```yaml
schema: 1
claims:
  - criterion: AC-001
    verification: component
    tests:
      - repository: claims-api
        tier: component
        id: Claims.ExpiredClaimIsRejected          # exactly as in the parsed results
        source: { file: tests/ClaimsTests.cs, symbol: ExpiredClaimIsRejected }
        change: new                                 # new | reused | modified, against the baseline
test_impacts:
  - test: { repository: claims-api, tier: component, id: Claims.ExpiredClaimIsAccepted }
    change: removed
    reason: Its expectation contradicts VR-001.
    replaced_by: [Claims.ExpiredClaimIsRejected]
```

## Satisfaction

A method is satisfied when its claimed tests exist, their `change` is allowed,
a review accepted this exact claims revision where `semantic_review: required`,
and every claimed test passed in the engine's results at the method's
repository and tier. A skipped test does not pass. A criterion is satisfied when
all its methods are; a requirement when all its supporting criteria are.
