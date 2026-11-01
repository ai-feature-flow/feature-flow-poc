---
name: solution_design
description: How to design or implement within a ratified design - grounding in current code, boundaries, contracts, and what to do when the design and the code disagree.
---

# Solution design

## Ground it in the code

Read the code a design touches before writing it. Name the actual
repositories, projects, modules and classes, and say what each is responsible
for and what it depends on. A design that could describe any system is not
grounded.

## Boundaries

Draw a boundary where responsibility or the rate of change differs, not to
match a pattern. Do not add controller, service and repository layers unless
the code needs them. Keep dependencies pointing one way, and say which.

## Contracts

State every interface another unit relies on: endpoints and their payloads,
messages, schemas, public types. A contract a child relies on is part of the
parent's ratified design: changing it later needs a new approval.

## Failure and change

Say how each path fails and recovers, what is validated where, and what happens
to existing data, callers and deployments: migration, compatibility, rollout.

## Decisions

Record each material decision with the alternatives considered and why they
were rejected, so a later reader does not reopen it without cause.

## When the design and the code disagree

If implementing a ratified design turns out to need a change to its contracts
or boundaries, do not make the change silently. Report the step `blocked` and
say what must change, or ask a person if your step allows questions. Small
choices the design leaves open are yours: make them in the repository's style
and mention them in your summary.
