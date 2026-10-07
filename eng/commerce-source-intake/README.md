# Commerce frozen source intake

This transport validates and assembles the fixed candidate and dependency graph.
It does not execute candidate code, start an SDK or Docker, accept native results,
deploy, or close source migration issues. The shared File producer is copied with
its reviewed SHA256 seal; ZIP validation and bounded same-repository fetch remain
owned by that shared implementation. The caller binds policy bytes before loading it.

The baseline checkout must have the exact raw file bytes named by policy.json.
The assembled directory contains no Git metadata. A separately admitted native
owner must establish trusted accepted-base ancestry and preserve the Auth private
Docker proxy boundary before build, focused/full tests, coverage, audit and cleanup.

Set `COMMERCE_BASELINE` to an accepted-base checkout with exact raw policy bytes, then
run `python -B -m unittest discover -s eng/commerce-source-intake`. The workflow
checks out the fixed accepted base separately and supplies this path. Four entrypoint
controls verify the actual capsule graph, source-only receipt and refusal boundaries.
The workflow validates local capsule bytes on pull requests. On protected main,
manual dispatch fetches the exact same-repository Git blob instead. Both lanes
report source assembly only. No policy, capsule OID, lane or producer runtime override exists.
