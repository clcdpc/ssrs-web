# Date parameter boundary acceptance

Verification of date parameter boundaries against known SSRS report data remains open. This is a backlog follow-up, not a merge blocker.

## Completed evidence

- Live DEV testing confirmed the SSRS defaults `beginDate = 9/1/2026 12:00:00 AM` and `endDate = 9/30/2026 11:59:59 PM` arrive as `2026-09-01T00:00:00` and `2026-09-30T23:59:59`.
- Synthetic regression coverage exercises the accepted date forms and SOAP parameter transport. The reconstructed fixture is not a new live capture.
- The [workspace reference](behavior/report-workspace.md#parameter-state-dependencies-and-dates) documents the current parsing and transport behavior.

This evidence does not establish how a report filters real rows at the boundary. No product defect has been observed, and this follow-up was deferred rather than treated as a merge blocker.

## Remaining verification

- Identify a read-only DEV report and trusted data set with known rows precisely at, immediately before, and immediately after the start and end date-time boundaries. An isolated synthetic fixture is an alternative when live evidence cannot be produced.
- Compare the stock SSRS portal and this portal with identical date ranges and other report parameters. Check that seconds survive default rendering, edits, refresh, and submission, and that there is no unexpected timezone conversion or truncation.
- Confirm the report's inclusive or exclusive filtering at midnight and `23:59:59`, separating report/query behavior from the parameter values transmitted by the portal.
- Add repeatable regression coverage where feasible without sensitive production data or changes to a live report.
- Record the report, inputs, expected rows, actual rows, and remaining limitations. Track any confirmed report/query discrepancy separately.

## Acceptance

A documented comparison against known DEV records verifies the expected boundary behavior. If live evidence cannot be produced, any synthetic substitute must be labeled as synthetic and must leave live acceptance open. Suitable records and expected/actual results are still required before claiming live acceptance. Priority remains backlog.
