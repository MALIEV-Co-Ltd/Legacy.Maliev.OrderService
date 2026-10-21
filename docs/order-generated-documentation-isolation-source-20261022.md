# Order generated documentation isolation

## Retained source scope

Original source `03dc9a1271c16e6535934445e9dd6e3f30e8fffe`, parent
`b1dc51ef5241ceb40a3c8a4e5c3f3a823d16b07e`, isolates generated XML documentation
from project source folders. The returned original commit metadata has 179 file
paths. This slice covers only the 14 Order/OrderStatus-owned paths below.
No whole-source, other-owner or complete documentation-content parity is claimed.

| Original project / removed XML | Current owner mapping |
| --- | --- |
| Maliev.OrderService.Api/Maliev.OrderService.Api.csproj and Maliev.OrderService.Api.xml | Current Order API; existing generation true, no root XML or copy override |
| Maliev.OrderService.Common/Maliev.OrderService.Common.csproj and Maliev.OrderService.Common.xml | Current Application/Domain own the consolidated contracts/entities; existing generation true |
| Maliev.OrderService.Data/Maliev.OrderService.Data.csproj and Maliev.OrderService.Data.xml | Current Order Data; existing generation true |
| Maliev.OrderService.Tests/Maliev.OrderService.Tests.csproj and Maliev.AuthService.Tests.xml | Current consolidated Order Tests; missing generation repaired |
| Maliev.OrderStatusService.Api/Maliev.OrderStatusService.Api.csproj and Maliev.OrderStatusService.Api.xml | Current consolidated Order API; existing generation true |
| Maliev.OrderStatusService.Data/Maliev.OrderStatusService.Data.csproj and Maliev.OrderStatusService.Data.xml | Current consolidated Order Data; existing generation true |
| Maliev.OrderStatusService.Tests/Maliev.OrderStatusService.Tests.csproj and Maliev.OrderStatusService.Tests.xml | Current consolidated Order Tests; missing generation repaired |

The source replaces explicit source-root DocumentationFile values with
GenerateDocumentationFile=true, removes None XML CopyToOutput items and deletes
the generated source artifacts. The original added cross-project regression
`Maliev.Web.Tests/GeneratedDocumentationIsolationTests.cs`, blob
`72e64c47410dd924b53fffad658b03e0fe2e7283`, is Web-owned. Its source/output
isolation assertions are adapted to this owner's five projects without claiming
the Web-owned path or importing the old monorepo.

## Actual current gap and minimal repair

Inspected main `2fdba243047ed25cf448d39b2976ae2bcf93b666`, tree
`f9848076ad7e71767b1069b9ba12a609c38dab5e`.
All four production projects already set GenerateDocumentationFile=true.
The actual test project preimage
`803c8ff913f5a5d51dfdff6f0b2f25345f472457` does not generate documentation.
There are no tracked project XML artifacts in the exact inspected tree.
No existing generated-documentation isolation test was found in that owner.

The only existing-file change adds GenerateDocumentationFile=true to the test
project. All existing references, packages, test settings and warning treatment
remain byte-identical. Directory.Build.props `c4d643b640e122a3ab278aa6e7157740511b07d1`
already contains the repository's CS1591 suppression; this slice adds no warning
suppression and does not change documentation policy for production assemblies.

## Authored verification

Five project rows adapt the retained source regression: reject DocumentationFile
overrides, reject None Update XML copy items, require generation and reject the
project-named source-root XML artifact. One further fact opens the actual XML
beside the executing test assembly, verifies its assembly name and verifies the
new documented test class is present among compiler-generated member entries.
It inspects compiler output rather than launching another build or test host.

The six cases are authored, not executed. This source-only lane creates no
native helpers, test hosts, containers, file writes, refs or CI runs.
Required hosted exact-head checks: Release build0warnings/errors; focused
GeneratedDocumentationIsolationTests with6passes and no skips; admitted affected
suite; unchanged format/static/security/audit/contract/coverage gates; independent
review/protected merge and separate exact protected-main acceptance.
Build the test assembly before running the output-artifact assertion.
No binary/XML execution result or passing build is inferred from source checks.

There are no runtime, DTO, serializer, navigation, provider, financial, status
transition, database, migration, dependency or workflow edits. Comment,
TrackingNumber, financial/provider/FX and the FileFormat wire candidate remain
held. Root remains sole publisher, official collector and canonical writer.

## Publication composition

This source proposal was inspected on base `2fdba243047ed25cf448d39b2976ae2bcf93b666`.
Publication composes the same three disjoint paths onto protected main
`bd7f84df547eb540bb93e29f6ccea500930a532d`, retaining PR #97's reviewed
latest-status date projection, HTTP regressions and formatting. Only a truthful
parameter tag is added to the new XML-output theory; runtime and warning policy
remain unchanged. Exact-head and post-merge validation are still required.
