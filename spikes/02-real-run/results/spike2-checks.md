Captured 2026-10-29T07:56:01Z.
Repository ai-feature-flow/feature-flow-example, checkout D:\Dev\feature-flow-poc\spikes\02-real-run\fixture, branch ff/ticket/spike-2-1 -> ff/epic/spike-2.
Temporal localhost:7433, queue ff-a3-queue, run checks-20261029075601.

## R5: a Docker-dependent tier runs through CI delegation

  manifest: sha256:a5c19dc3243bbb480f7c3d139f0deb17a43b683173b950ff828bedf14a40038c (926 bytes) on ff/ticket/spike-2-1
  tiers declared: unit, integration

  tier unit: target=sandbox workflow=unit.yml
  tier integration: target=ci workflow=integration.yml

  Docker-dependent tier: integration
  head: 6384266770caeade2afcd2937f0f410acb59a138

  dispatched:  run 40838618182 workflow=integration.yml event=workflow_dispatch
  url:         https://github.com/ai-feature-flow/feature-flow-example/actions/runs/40838618182
  returned in: 5.3s (without waiting for the run)

  poll  1: state=Queued conclusion=None checks=0/1 registered=True
  poll  2: state=InProgress conclusion=None checks=0/1 registered=True
  poll  3: state=InProgress conclusion=None checks=0/1 registered=True
  poll  4: state=Completed conclusion=Success checks=1/1 registered=True

  conclusion:  Success
  url:         https://github.com/ai-feature-flow/feature-flow-example/actions/runs/40838618182
  workflow matches the manifest mapping (integration.yml): True
  tier commands the workflow runs: docker compose -f compose.integration.yaml up --abort-on-container-exit --exit-code-from integration-tests

  **PASS**

## R6: credentials are short-lived, scoped to one repository, and expire

  credential id:   gh-3-1793260602
  target system:   github
  scope:           repo:ai-feature-flow/feature-flow-example contents:write metadata:read pull_requests:write
  issued at:       2026-10-29T07:56:42.5078120+00:00
  expires at:      2026-10-29T08:56:42.0000000+00:00   (from the remote, not computed)
  ttl:             60.0 min
  secret:          390 chars, value withheld (sha256:537823f4720b)
  actor:           agent:claude-code (run checks-20261029075601, step implement)
  permissions:
    contents: write
    metadata: read
    pull_requests: write

  short-lived (ttl 60.0 min <= 60): True
  scoped to one repository: True
  not expired 1 min after issue: True
  expired 1 min past expiry:     True

  refusal (a wider scope must be refused, never granted):
    wider access level:  refused=True (Work/forbidden)
    another repository:  refused=True (Work/forbidden)
    ttl over the maximum: refused=True (Work/forbidden)

  **PASS**

## R13: tokens, model and compute time recorded per step

  NOT EVIDENCED: no cost records exist for this run. Run the ticket first
  (RealHarness run), then re-run this check.

  **NOT EVIDENCED**

## R4: a review run twice on unchanged input gives the same verdict

  diff hash:      ee023ab17ec5474d737039e0760ff4ca58f380d9bfa54879590ae83136b9148b
  prior findings: 1 (f-r4-1)
  declined:       0 ()

  reviewer unavailable: Work/unparseable: the reviewer did not return a verdict object: 

  **NOT EVIDENCED**: R4 needs a real reviewer model; the router call failed.

## R16: a mid-run edit to feature-flow.yaml is picked up by the next step

  step N read:   ref=ff/ticket/spike-2-1 sha256:a5c19dc3243bbb480f7c3d139f0deb17a43b683173b950ff828bedf14a40038c (926 bytes)
  mid-run edit:  committed fedf5e7572be09f4f93ecd31039ee4499efbdc78 to ff/ticket/spike-2-1

  step N+1 read: ref=ff/ticket/spike-2-1 sha256:a62ee0c1b3d0fce344ebbc8dd708077a72f756f3d213d351a5feb013c84bf92e (955 bytes)

  hash changed between the two steps: True
    a5c19dc3243bbb480f7c3d139f0deb17a43b683173b950ff828bedf14a40038c -> a62ee0c1b3d0fce344ebbc8dd708077a72f756f3d213d351a5feb013c84bf92e
  the edit is visible in the content the next step read: True
  steps whose cost record carries the manifest hash: 0/0

  **PASS**

## R8: a gate left open does not time out, and a reminder fires

  workflow r8-1793260640 started; the gate is open and nobody has answered it

  gate contract (docs/06 3.1): no timeout; reminders are timers, not timeouts
  heartbeat timeout in force: 00:00:30

    t+   20s  still waiting=True  terminal=(none)  reminder #1 fired
    t+   40s  still waiting=True  terminal=(none)  reminder #2 fired
    t+   60s  still waiting=True  terminal=(none)  reminder #3 fired
    t+   80s  still waiting=True  terminal=(none)  reminder #4 fired
    t+  100s  still waiting=True  terminal=(none)  reminder #5 fired
    t+  120s  still waiting=True  terminal=(none)  reminder #6 fired

  after 120s: still waiting=True terminal=(none)
  reminders fired while it waited: 6

  control: approve the gate
  result after approval: completed

  NOTE: the check waited 2 min in real time, not hours. The
  mechanism that survives hours is the durable wait itself: the workflow holds
  no timer and no activity while the gate is open, so there is nothing to time out.

  **PASS**

## K4: seed a finding, fix it, run fix.verify (regression test required)

  seeded finding: f-k4-1 (merge-bar/null-guard) at src/FeatureFlow.Example.Api/WidgetStore.cs

  full diff hash: b78f2d67ff57dbc4f0b751451bef91329590b767ad900215d34bd9b0909c0e13 (3006 bytes)

  case 1: the fix lands with no regression test
    regression test present: False
    'not applicable' reason: (none)
    fix.verify would pass:   False  (must be false)

  case 2: the same fix with a regression test added
    regression test present: True  (must be true)
    fix.verify would pass:   True

  case 3: 'not applicable' with a recorded reason (docs/06 10.1)
    no reason recorded: fix.verify would pass: False  (must be false)

  NOTE: the 'not applicable with a reason' escape is carried in the finding's
  triage record, not in the plan. A template that could set it to false would
  let a workflow relax a verification floor (review 09 C4).

  running fix.verify through the real activity:
    outcome: completed
    reason:  (none)
    completed: True

  **PASS**

## review 11 C1: actual outputs sizes per step kind

  kind                           steps  entries  max bytes  total bytes  vs cap
  ------------------------------------------------------------------------------------
  agent.run                      4      5        40         104          within
  ci.wait_checks                 1      2        7          8            within
  findings.evaluate_readiness    1      2        4          5            within
  findings.triage                1      2        6          7            within
  fix.verify                     1      2        59         65           within
  human.gate                     4      1        8          8            within
  plan.replan                    1      1        26         26           within
  review.run                     1      3        71         81           within
  scm.merge                      1      3        40         49           within
  scm.open_pr                    1      2        40         41           within
  test.run_tiers                 1      3        16         34           within

  widest single value: review.run at 71 bytes (cap 256)
  most entries:        agent.run at 5 (cap 16)

  every step kind published within the cap of 16 entries and 
  256 bytes per value. The cap is not binding for this plan, so
  the measurement does not justify changing it — it justifies keeping it.
  wrote D:\Dev\feature-flow-poc\spikes\02-real-run\results\outputs-sizes.json

  **PASS**

## K4: the decline path (reason posted, finding not re-raised)

  seeded finding: f-decline-1 (merge-bar/magic-number)
  open before decline: 3 (f-r4-1, f-k4-2, f-decline-1)

  could not open a PR: Infrastructure/server_error 
  NOT EVIDENCED: no pull request could be opened for the decline, so the
  decline reason cannot be posted on a real PR.

  **NOT EVIDENCED**

## K5: approve a gate from chat, then drop one webhook and poll instead

  part 1: approve a gate from chat (inbound event path)

  workflow k5-chat-1793260776 started; the gate is open
  gate open (workflow waiting): True
  chat approval delivered as a Temporal update (the inbound event path)
  result: completed

  part 2: drop the webhook deliberately, and poll instead

  workflow k5-poll-1793260777 started; the gate is open
  the inbound event is deliberately NOT delivered (the webhook is dropped)
  gate open (workflow waiting): True

  polling fallback: reading the remote state instead of waiting to be told
    poll 1: gate still open, no inbound event arrived
    poll 1: fallback delivered the decision from the polled state
    poll 2: gate resolved to approved

  result after the polling fallback: completed
  polls: 2

  NOTE: the webhook drop is simulated by not delivering the update. A real
  drop would be a lost inbound HTTP delivery; the fallback is the same either
  way, because the step's source of truth is the remote state, not the event.

  **PASS**

## Summary

checks run:        10
failed or not evidenced: 3
  R13, R4, K4-decline

