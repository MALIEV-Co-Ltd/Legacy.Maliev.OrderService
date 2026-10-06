# Literal process names

Unpublished draft based on accepted Order main `5495335345fc6b234f0d8eb4fc95f18239b9fdcf`.
Original `5fac706a7983a6d359b39acbd670e6800afe020e` and latest source snapshot
`135e526d0dab85c415b3afdcefd7b70fe2c82e2f` have the same ProcessesController blob
`d65fe4fff0427579467baf94333117776451de11` at
`Maliev.OrderService.Api/Controllers/ProcessesController.cs`.
Both CreateProcessAsync and UpdateProcessAsync assign Name directly from the request.
The current target silently strips leading/trailing whitespace in both writes.

The production draft removes exactly those two Trim calls. It preserves CategoryId, timestamps,
routes, DTOs, permissions, validation, category filtering, deletion and ID ordering. Navigation-graph
omission remains the documented separate target DTO adaptation; it is not changed or claimed closed.

The existing four normal Program/PostgreSQL/JWT category lifecycle cases now use literal names
containing significant spaces/tabs, ASCII-escaped Thai characters and literal percent/underscore.
They assert exact POST response, Location GET, category/all-list, detail GET and actual persisted
names before/after PUT, preserved CategoryId/CreatedDate and untouched unrelated process scalars.
DELETE behavior remains covered. No new test case is added: source focus27 and full324 stay unchanged.

Native build, tests, formatting and coverage are pending hosted validation after full actual diff review.
No local .NET, SDK, testhost or Docker execution; no publication, deployment or whole-initial closure.
Require fresh full324, nullable-sort24, master-query27 and source27, raw four-assembly coverage >=80 percent,
generated lines retained without exclusions, build zero warnings/errors and all static/security checks.
