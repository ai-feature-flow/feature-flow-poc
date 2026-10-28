Captured 2026-10-28T16:25:11Z.
Repository ai-feature-flow/feature-flow-example, checkout D:\Dev\feature-flow-poc\spikes\02-real-run\fixture, branch ff/ticket/spike2-final -> main.
Temporal localhost:7333, queue spike-2-real, run final-run.

## R5: a Docker-dependent tier runs through CI delegation

  manifest: sha256:2f507523ca580eade86f36e3696f6d1473f0399466ff49664cf37cdbb7f129c8 (985 bytes) on ff/ticket/spike2-final
  tiers declared: unit, integration

  tier unit: target=sandbox workflow=unit.yml
  tier integration: target=ci workflow=integration.yml

  Docker-dependent tier: integration
  head: df3d0f41322b444f9b58cd4f9a76c849826158b7

  dispatched:  run  workflow=integration.yml event=workflow_dispatch
  url:         
  returned in: 22.7s (without waiting for the run)

  poll  1: state=Completed conclusion=Success checks=1/1 registered=True

  conclusion:  Success
  url:         https://github.com/ai-feature-flow/feature-flow-example/actions/runs/40723029504
  workflow matches the manifest mapping (integration.yml): True
  tier commands the workflow runs: docker compose -f compose.integration.yaml up --abort-on-container-exit --exit-code-from integration-tests

  **PASS**

## R6: credentials are short-lived, scoped to one repository, and expire

  credential id:   gh-3-1793204737
  target system:   github
  scope:           repo:ai-feature-flow/feature-flow-example contents:write metadata:read pull_requests:write
  issued at:       2026-10-28T16:25:37.4754537+00:00
  expires at:      2026-10-28T17:25:37.0000000+00:00   (from the remote, not computed)
  ttl:             60.0 min
  secret:          390 chars, value withheld (sha256:b66755d3ab87)
  actor:           agent:claude-code (run final-run, step implement)
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

  records: 13

  step                 kind                   model                in       out      compute   source
  ----------------------------------------------------------------------------------------------------
  plan_spec            agent.run              qwen2.5-coder:7b     2050     57       6.9       runtime_usage_output
  design               agent.run              qwen2.5-coder:7b     2050     28       6.7       runtime_usage_output
  implement            agent.run              qwen2.5-coder:7b     2050     26       17.8      runtime_usage_output
  run_tests            test.run_tiers         -                    -        -        45.1      none
  review               review.run             -                    -        -        2.3       none
  evaluate_readiness   findings.evaluate_readiness -                    -        -        0.9       none
  open_pr              scm.open_pr            -                    -        -        4.7       none
  open_pr              scm.open_pr            -                    -        -        3.4       none
  wait_checks          ci.wait_checks         -                    -        -        2.4       none
  merge_pr             scm.merge              -                    -        -        5.8       none
  verify_fix           fix.verify             -                    -        -        1.9       none
  verify_fix           fix.verify             -                    -        -        1.8       none
  verify_fix           fix.verify             -                    -        -        1.9       none

  steps with a model recorded:  3/13
  steps with tokens recorded:   3/13
  steps with compute recorded:  13/13

  run total: in=6150 out=111 compute=101.4s
  every step recorded compute time: True
  agent.run steps reporting usage: 3/3 (source: runtime_usage_output)

  **PASS**

## R4: a review run twice on unchanged input gives the same verdict

  diff hash:      63cec8a2fee2f3349bb65075611445e7b293d2bca74c529f682f3e628f5a5eae
  prior findings: 1 (f-r4-1)

  pass 1: outcome=findings_open findings=1 prior=1
  pass 2: outcome=findings_open findings=1 prior=1

  same verdict:            True
  same finding set:        True
  prior findings passed:   True (1)

  control: decline the finding, then re-run the verdict
  after decline: outcome=completed findings=0
  verdict changed when the prior findings changed: True
  declined finding stays declined: True

  **PASS**

## R16: a mid-run edit to feature-flow.yaml is picked up by the next step

  step N read:   ref=ff/ticket/spike2-final sha256:2f507523ca580eade86f36e3696f6d1473f0399466ff49664cf37cdbb7f129c8 (985 bytes)
  mid-run edit:  committed 1bfe649a8c1b1386f5197d5b51676eca921c2f1b to ff/ticket/spike2-final

  step N+1 read: ref=ff/ticket/spike2-final sha256:d4127d26e52d22bdc79a7e2bb991b019cab719e0b59d95bc5007eec2d32db951 (1015 bytes)

  hash changed between the two steps: True
    2f507523ca580eade86f36e3696f6d1473f0399466ff49664cf37cdbb7f129c8 -> d4127d26e52d22bdc79a7e2bb991b019cab719e0b59d95bc5007eec2d32db951
  the edit is visible in the content the next step read: True
  steps whose cost record carries the manifest hash: 12/13

  **PASS**

## R8: a gate left open does not time out, and a reminder fires

  workflow r8-1793204745 started; the gate is open and nobody has answered it

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

  full diff hash: 00d9584b608afe0eaf0b1b0524487b786eccee3d9bf4e1f77ef2c38dfd0e9b97 (1049 bytes)

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
  open before decline: 2 (f-k4-2, f-decline-1)

  the run's PR #3 is unreadable; opening one for the decline
  opened PR #4 on ff/ticket/final-run-decline-20261028162752 for the decline
  decline posted on PR #4: https://github.com/ai-feature-flow/feature-flow-example/pull/4#issuecomment-6474611808
  reason: accepted: the id sequence is internal to the store and is not part of the API contract

  finding recorded as declined: True
  next review's prior findings: 1 (f-k4-2)
  the declined finding was re-raised: False  (must be false)
  verdict with the finding declined: completed (findings=0)
  verdict is not findings_open: True

  **PASS**

## K5: approve a gate from chat, then drop one webhook and poll instead

  part 1: approve a gate from chat (inbound event path)

  workflow k5-chat-1793204883 started; the gate is open
  gate open (workflow waiting): True
  chat approval delivered as a Temporal update (the inbound event path)
  result: completed

  part 2: drop the webhook deliberately, and poll instead

  workflow k5-poll-1793204885 started; the gate is open
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
failed or not evidenced: 0

