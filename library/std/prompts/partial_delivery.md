# Delivery merged some repositories but not all

Ordered delivery stopped before every repository merged. The delivery report
lists, per repository, whether its merge happened.

| Answer | Effect |
|---|---|
| `retry` | Delivery runs again from the first repository that has not merged |
| `fix_forward` | The run ends `partial`; the merged repositories stay merged and the remaining work is done in a new run |
| `revert` | The engine opens a revert pull request for each repository this run merged, then the run ends `partial` |
