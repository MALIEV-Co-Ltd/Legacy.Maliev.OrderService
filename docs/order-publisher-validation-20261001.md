# Dormant Order publisher validation adoption

## Ownership and scope

Exclusive worktree `B:/maliev-legacy/.worktrees/order-publisher-validation-20261001`,
branch `codex/order-publisher-validation-20261001`, clean canonical/live-origin
base `053ececa11bead6fbcf9db8da2ddd6c0a242b641`. Other worktrees and original
source objects remain untouched. No commit, push, GitHub write or deployment.

Publisher changes only the reusable workflow pin to reviewed producer
`503e8846390a597c267d2889b33a9c26863389b3` and adds job-local `actions: read`.
Workflow-level permissions remain only contents:read; publisher permissions are
exactly contents:read, actions:read, id-token:write. Deployment remains gated by
unchanged LEGACY_DEPLOY_ENABLED conditions. All six inputs, Docker context/file,
environment, publisher identity variable and artifact registry variable remain
unchanged. No grants, WIF provisioning, variable activation, image publication,
GitOps deployment or runtime change is claimed.

Reviewed producer requires exact protected-main ci-main validation before its
publish job, including current run/attempt/job proof and rechecks. Publication
still uses the exact checked-out caller SHA and immutable registry digest.
This caller adoption supplies the actions-read capability for that inspection;
it does not locally execute the hosted GitHub/provider/publication boundary.

## Individual committed source cohort

Source mirror authority cutoff `bed10c7d15e0698e0b75f1329d0f312937f5d77f`:

| Source SHA | Order-specific behavior represented | Bounded adoption disposition |
|---|---|---|
| `00ec830615c15b5e4e227046712247b11df0100f` | Order deploy.ps1 native-command failure propagation and cleanup | Adopt reviewed reusable validation/publication failure boundary; no restoration of imperative deployment scripts. |
| `72163e9ae11f39f6579423841a2e20529b986fab` | Deploy wrapper/script exit status propagation | Preserve producer fail-closed validation/publication status through exact pinned reusable job; hosted execution remains integration evidence. |
| `f8921b1b1d5846eeaff999af10b640011655d1d4` | Render throwaway manifest instead of mutating template | Current publisher owns immutable image digest, not manifest mutation; original manifest/runtime rollout behavior remains separately gated. |

Each source commit was inspected independently from committed mirror objects.
This is not whole-commit/fleet/source-owner closure and does not claim staging,
rollback, capacity, secret, migration or old-writer readiness.

## Test-first evidence and private graph

Existing regex OIDC-scope control was retained unchanged. New parsed YAML checks
verify exact producer pin, three minimal job permissions, workflow read-only
permission, both dormant gate conditions and all six unchanged inputs. Seven
mutants reject removal of each required permission, elevation of contents/actions,
incorrect id-token permission and an extra packages grant. Each mutant first
validates the unmodified current document, so invalid baseline cannot satisfy it.

Fresh initial Release build0warnings/0errors. New intended RED executed before
YAML repair:19passed/2failed/0skipped, missing actions permission and old73dd pin:
`TestResults/order-publisher-red/natth_MALIEV-31USFIV_2026-10-01_14_04_59_net10.0.trx`.
Fresh repaired Release0warnings/0errors; focused23passed/0failed/0skipped:
`TestResults/order-publisher-focus/natth_MALIEV-31USFIV_2026-10-01_14_05_42_net10.0.trx`.

Owned ignored `.dependencies` clones are clean and exact CI pins:
Defaults `8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3`, CompatibilityContracts
`78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7`. Each process sets
UseLocalMalievDependencies=true and absolute MalievWorkspaceRoot to this owned
directory. No shared/sibling build output is used. Existing build workflow,
Dockerfile, Program and private dependency SHAs were verified unchanged.

## Commands and acceptance status

```powershell
dotnet build Legacy.Maliev.OrderService.slnx -c Release -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=$env:MalievWorkspaceRoot -nodeReuse:false
dotnet test Legacy.Maliev.OrderService.Tests/Legacy.Maliev.OrderService.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~PublishWorkflowPermissionContractTests|FullyQualifiedName~WorkflowContractTests' --logger trx --results-directory TestResults/order-publisher-focus
dotnet test Legacy.Maliev.OrderService.slnx -c Release --no-build --no-restore --logger trx --results-directory TestResults/order-publisher-full
actionlint
```

Final whole suite:237passed/0failed/0skipped, duration1minute36seconds;
`TestResults/order-publisher-full/natth_MALIEV-31USFIV_2026-10-01_14_05_44_net10.0.trx`.
XML counters independently read back total/executed/passed237, failed/notExecuted0.
Actionlint1.7.12 whole-repository check, whole-solution format verification and
git diff --check passed. Transitive vulnerability audit reports no vulnerable
packages in all five solution projects. Gitleaks history52commits and the scoped
three-file stdin scan passed. Exact CI73dd signing-resource scanner passed across
the final tracked-plus-new candidate (no signing material). No coverage-floor claim is made:
this run has no coverage collector; runtime and existing coverage inputs remain
unchanged. Root owns independent acceptance/protected merge.

No hosted publisher, cloud credential exchange, registry push or deployment was
executed. Accepted shared-producer CI is a prerequisite, not local proof of
enabled Order publication. All owned build/test/static handles are terminal
before release; no commits or pushes were performed.

## Independent integration evidence

Bounded issue49 owns this adoption; broader source-owner and application
acceptance remain separate. Root reviewed the complete three-file candidate and
confirmed the reusable workflow input defaults are unchanged between the old
73dd pin and reviewed503e. Independent Release solution build had zero
warnings/errors, focus23 and full237 passed without skips. Existing solution
configuration maps private shared projects to Debug; application projects remain
Release, and no dependency/build configuration was edited. Root TRXs are in
`TestResults/root-publisher-focus` and `TestResults/root-publisher-full`.
