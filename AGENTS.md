# Legacy.Maliev.OrderService agent guidance

- Preserve 55 actions and 56 route templates across orders, files, processes, categories,
  file formats, statuses, available transitions, and history.
- Keep Order and OrderStatus in separate databases/DbContexts on `legacy-postgres-order`.
- Preserve computed Remaining, Subtotal, and Turnaround behavior and all legacy names/FKs.
- Customer, Employee, Material, Color, SurfaceFinish, Currency, Quotation and Accounting IDs are
  external scalar references; never add cross-domain DbContexts or foreign keys.
- Status writes must follow `OrderStatusHasPossibleStatus`, serialize concurrent transitions,
  preserve all 14 named shortcut routes, and record append-only history through the transition API.
- All routes are JWT authenticated and permission-protected. Critical writes require live checks;
  creates use hashed Redis idempotency and mutable rows use ModifiedDate concurrency.
- GCS rows contain bucket/object metadata only and use ADC/Workload Identity. Never copy source
  credentials, NLog DB logging, keys, signed URLs, or service-account files.
- Use .NET 10, Scalar/OpenAPI, Npgsql, Redis, standard service defaults, and built-in logging.
- Existing GKE and `maliev-legacy` only; no new node pool, Cloud SQL, or paid infrastructure.
- Validate release build, route/model/transition tests, PostgreSQL 18 for both DBs, formatting,
  dependency vulnerabilities, and gitleaks before coherent commits.
