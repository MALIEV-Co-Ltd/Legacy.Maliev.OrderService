# Dormant Order rollout source

This owner template is deliberately disabled: replicas stay zero and the deployment-enabled annotation stays false after rendering. The single-replica activation target is recorded as one; RollingUpdate preserves surge one and zero unavailable from the exact source path bindings in source-obligations.json. This is a source adaptation for the consolidated modern Order API, not permission to activate it.

Run scripts/Render-DormantLegacyOrderDeployment.ps1 with an explicitly approved immutable owner image repository/digest and explicit existing service-account/runtime-secret references. Output goes only to stdout, is deterministic, and remains disabled. The renderer checks the exact template digest and rejects mutable/foreign/zero-digest images or unsafe references. It neither discovers nor provisions identities/secrets and cannot verify their existence or authority.

The JSON-form template is also YAML. Current owner liveness/readiness routes replace old split-service probe routes. No Service, ingress, identity object, database, migration, logging change or active GitOps inclusion is supplied. Offline render checks prove only this disabled transformation; scheduling, capacity, health/drain durations, identity, image provenance and production rollout require separate owner evidence. Neither mixed source commit is wholly closed by this artifact.
