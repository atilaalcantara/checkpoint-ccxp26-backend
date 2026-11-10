# Implementation status

> Earlier entries are a historical log; the current contract is `../Frontend/CONTRATO_API.md` and the latest update is at the bottom.

## Specification and assumptions (2026-11-10)
- Authority: `../Frontend/CONTRATO_API.md` and `REQUISITOS_BACKEND.md`, read in full. Backend initially empty; no applicable AGENTS.md found in project tree.
- SDK observed: 10.0.300. PostgreSQL: existing `postgres-dev`, 18.6 aarch64, preserved. Only application schema `checkpoint` will be migrated. Destructive integration tests use a separate temporary database.
- Adopt provisional contract choices: eligible results have at least one vote; frozen 10-minute snapshots; automatic recent-photo publication without moderation; original and AVIF thumbnail in bytea; 10 MiB/40 MP defaults; HEIC rejected. Animated/multi-page images will be rejected.
- Persist only completed idempotency operations, in the event transaction. Transaction-scoped visitor locks serialize competing writes; no durable processing reservation can become abandoned. Image processing happens before the short event transaction, with bounded concurrency. A crash before commit leaves no effect; after commit retry returns the stored result.
- Levels/art metadata are private configuration/files; no static file middleware. Local source is the frontend's actual AVIF assets. Frontend remains unchanged and still exposes future assets until a separate integration task.
- Snapshot storage bounded per visitor with expiration cleanup; offsets validated. No change to product rules identified.

## Traceability
| Requirement | Implementation target | Verification target |
|---|---|---|
| XP boundaries / private current artwork | Progression, progress endpoint | domain boundaries; HTTP actual AVIF and future-level absence |
| Stable <=3 assignments, waiting state | participation transaction + DB constraints | concurrent PUT; shortage/fill integration |
| Vote/photo exactly once + recovery | visitor lock, composite FKs, unique keys, event transaction | retries, changed payload, concurrent requests, operation lookup, XP query |
| Results authorization + stable pagination | frozen owned snapshot | locked, ordered pages, mutation, wrong owner, expiry |
| Actual validated image + AVIF | bounded NetVips processor | real codecs, dimensions, orientation, invalid/oversized inputs on Ubuntu ARM64 |
| Deployment / persistent schema | EF migrations, seed, Noble Dockerfile | restore/build/tests/format; ARM64 HTTP against PostgreSQL |

## Tasks
1. Inspection/specification: DONE.
2. API/persistence/migrations/seed: DONE (verified on Mac, in ARM64 container, and against the existing PostgreSQL schema `checkpoint`).
3. Image pipeline + focused tests: DONE (19 tests: 13 domain cases, 6 PostgreSQL integration scenarios).
4. Ubuntu ARM64 validation + documentation: DONE (README, api.http, OpenAPI in Development).

## Evidence
- `dotnet --list-sdks`: 8.0.404, 9.0.100, **10.0.300**.
- `docker ps`: existing postgres-dev up, bound only to 127.0.0.1:5432.
- `docker exec postgres-dev psql -U dev -d postgres -c 'SELECT version();'`: PostgreSQL 18.6 aarch64.

Next: implement a compact API with explicit transactional persistence and focused integration tests.

## Iteration log
- Initial build: succeeded, zero warnings/errors. Uses EF Core for versioned SQL migrations, Npgsql/Dapper for explicit transactional queries; no unnecessary tracking model.
- Test analyzers caught three `Assert.Single(Where(...))` usages; changed to predicate overloads.
- Noble SDK image contains **10.0.403**, whereas Mac has 10.0.300. Initial build correctly rejected latestPatch roll-forward. Changed to latestFeature within .NET 10, preserving minimum installed Mac SDK; Docker build then passed.
- WebApplicationFactory applies configuration after initial Program execution. First integration run exposed early connection resolution; moved resolution into DI factories. Next run: five integration scenarios passed; shortage fixture failed because PostgreSQL has no min(uuid). Replaced with ORDER BY id LIMIT 1 and added zero-poll assertion.
- Test databases are randomly named checkpoint_test_* and removed by fixture teardown, including startup-failure cleanup.

- Assignment test rerun: passed. Full suite in `mcr.microsoft.com/dotnet/sdk:10.0-noble` linux/arm64 (uname aarch64, SDK 10.0.403): **19/19 passed**; Mac osx-arm64 (SDK 10.0.300): 19/19 passed. No leftover `checkpoint_test_*` databases.
- Migration + demo seed applied to existing postgres-dev (18.6) via ARM64 image: 5 polls / 10 options, history row `202611100001_InitialSchema`. Schema `public` untouched.
- Live HTTP exercise (API in ARM64 container -> postgres-dev): health live/ready 200; progress at 0 XP returns only Desconhecidos (two AVIFs 191x448 and 184x448, no future data); participation gives 3 distinct polls; results 403 before 3 votes; vote 201, replay 200 same operationId, same key/other content 409 IDEMPOTENCY_KEY_REUSED, other key 409 ALREADY_VOTED; third vote unlocks results and XP=3 -> level `friends` with `previousLevels` only strangers; invalid file 422 and XP unchanged; real 1,035,988 B JPEG -> 201, XP +2, replay 200, other key 409 PHOTO_ALREADY_SUBMITTED, operation lookup matches, recent photo is real AVIF (`ftypavif`) 360x330; `/PrivateAssets/...` 404; CORS preflight allows only configured origin and the 3 headers.
- Image measurement (ARM64 container under Docker Desktop, 1 request): thumbnail 322 ms, 18,672 B from a ~1 MB JPEG; container memory ~78 MiB idle; libvips 8.18.7. Not a production performance claim: a laptop VM does not predict the target instance.
- Dependency issue found and fixed: Microsoft.AspNetCore.OpenApi pulled Microsoft.OpenApi 2.0.0 (CVE-2026-49451, treated as error). Pinned 2.7.5 (patched); `dotnet list package --vulnerable --include-transitive` clean.
- `dotnet format --verify-no-changes`: whitespace fixes applied, now exit 0.
- OpenAPI `/openapi/v1.json` 200 in Development, 404 in Production (also `/` is 404 in Production).
- Cleanup: all rows created by my manual HTTP run were deleted (single visitor verified first) and community_progress reset to 0/0; demo polls retained. Temporary containers removed.

## Open items / not verified
- HEIC orientation (irot) was not tested with a rotated iPhone file; libheif applies it, but no such fixture was available. HEIC decoding is only available in the Linux image, not in plain `dotnet run` on the Mac.
- Concurrency tests use 8 parallel requests on one visitor; no load test, no multi-instance test.
- Throwaway verification of crash-before-commit recovery relies on transaction semantics (nothing persists before commit); not simulated with a process kill.
- Frontend untouched; it still publishes future level art.

## Update: broad image input (HEIC and others)
- Request: accept and convert as many photo formats as possible, especially HEIC.
- Finding: bundled libvips 8.18.7 opens HEIC headers but fails at pixel decoding (no HEVC). Ubuntu Noble apt libvips 8.15.1 + libheif 1.17.6 + libde265 decodes it.
- Change: container uses system libvips (`UseSystemVips=true`); ImageProcessor now identifies formats via libvips against a loader allowlist (JPEG, PNG, WebP, HEIC/AVIF, GIF, TIFF, JPEG 2000, JXL, and BMP/PSD/ICO through signature-gated ImageMagick). SVG/PDF and others refused. First frame only; `FailOn.Error` instead of `Warning` (iPhone HEIC emits a harmless nclx warning that was a rejection risk); alpha flattened to white; ICC to sRGB; AV1 compression made explicit.
- Failures hit and fixed: `av1` string rejected as enum (use typed enum); `thumbnail_image output_profile` absent in libvips 8.15 (replaced by explicit `IccTransform`), which only surfaced when testing the Ubuntu build.
- Evidence: Ubuntu ARM64 suite 21/21 (HEIC 4K: 402 ms, 22 KB thumbnail); Mac suite 21/21 (HEIC correctly 415 there); live HTTP in ARM64 container accepted HEIC (630 KB, 2.9 MB, 5.8 KB), JPEG, PNG, TIFF, BMP, GIF, JPEG 2000, PSD; refused PDF and SVG (415); 47 MB TIFF/BMP/PSD hit 413. Test data removed afterwards (XP back to 0/0).

## Update: input formats narrowed
- Decision: accept only JPEG, PNG, WebP, AVIF, HEIC/HEIF and JPEG 2000. GIF, TIFF, BMP, PSD, ICO, JXL and the ImageMagick path were removed (`AllowMagick` setting deleted).
- Finding: the Mac NuGet libvips cannot read JPEG 2000 (my earlier probe checked only that a loader type existed, not that it loaded). The Linux image can. Test now converts where the loader exists and expects 415 otherwise, like HEIC.
- Evidence: Ubuntu ARM64 suite 21/21; Mac 21/21; live HTTP in the ARM64 container: HEIC (two files) and JPEG 2000 -> 201 and stored as AVIF; TIFF, GIF, BMP -> 415. Test data removed (XP 0/0, no leftover test databases). A Docker Desktop restart happened mid-run; postgres-dev came back by its restart policy with data intact (5 polls).

## Update: Swagger UI and OpenAPI
- Added development-only Swagger UI `/swagger` over the existing generated `/openapi/v1.json`, with English operation descriptions, visitor/idempotency headers, multipart upload, typed response/error schemas and status documentation. Response models are shared with runtime serialization; status metadata and descriptions must be maintained when endpoint behavior changes.
- Added an integration check for the generated contract, including operation-result alternatives. Full Mac suite: 22/22 passed; format verification passed; NuGet vulnerability scan clean. Noble ARM64 final image built successfully; actual HTTP returned 200 for Swagger and OpenAPI in Development and 404 for both in Production. Temporary containers stopped.

## Update: simplified write recovery
- Supersedes the earlier idempotency-key and operation-lookup decisions: writes require only `X-Visitor-Id`. Duplicate votes and photos return 409; `PUT /participation` supplies `answerOptionId` and `photoStatus` for reconciliation. A still-running upload can commit after an initial status read; the client must recheck. Photo status does not identify the uploaded file.
- Removed operation endpoint, key validation, replay logic and operation fields in success responses; visitor locks, database constraints and transactionally awarded XP remain. Historical migration and legacy operation table remain untouched to preserve existing data.
- Updated frontend contract, historical-document notices, Swagger metadata and request examples. Full suite: 22/22 on Mac and 22/22 in Ubuntu ARM64 against isolated real PostgreSQL databases; format verification passed. Concurrent vote and photo tests confirm one award and recovery through participation.

## Update: complete live results without presentation fields
- `GET /results` returns the complete list of visible polls with votes, ordered by total descending and stable ID; every call gets current data, after the three-vote authorization check. No pagination or new stored snapshots. The initial schema's unused snapshot table and presentation columns are retained to preserve existing data and migration history; the old snapshot cleanup worker is no longer run.
- `iconKey` and `tone` are removed from participation DTOs and demo seed. Swagger, current frontend API contract and backend README updated. The existing mock frontend, animations and periodic refresh are not integrated in this backend task.
- Tests check authorization, more than 20 results, zero-vote/hidden exclusion, stable ties, new votes changing ranking, repeated refreshes without snapshot limits, no new snapshots and absence of presentation fields. Full suite: 22/22 Mac and 22/22 Ubuntu ARM64 with isolated PostgreSQL; the affected results test was rerun and passed on both after adding the final repeated-refresh assertion. Format verification passed.

## Update: alphabetical API ordering
- Results now use case-insensitive `pt-BR` title ordering with poll ID as tie-breaker; votes still refresh but do not change the order returned by the API. The frontend is responsible for the visual ranking by votes. Contract, Swagger description, and README updated.
- Targeted results and OpenAPI integration tests passed (2/2 on Mac and Ubuntu ARM64, isolated PostgreSQL); title accents, equal-title tie-breaking, fresh votes and stable API ordering covered. Format verification passed. Full suite was not rerun for this ordering-only change.

## Update: CORS for frontend integration
- Development allows any origin; other environments use configured explicit origins and reject `*`. All allowed origins can read the `Retry-After` response header; no credentials are enabled. Configuration is resolved through DI, so host overrides are honored.
- HTTP integration tests passed 3/3 on Mac and Ubuntu ARM64 against isolated test PostgreSQL: arbitrary Development preflight, actual Development 503 with exposed `Retry-After`, configured Production origin and denied unrelated origin, and actual Production 503 with exposed header. Initial Production test exposed early configuration resolution; moving CORS options to DI fixed it. Format verification passed. Full suite was not rerun for this scoped change.

## Update: configurable XP thresholds
- The four cumulative XP thresholds are now in `src/Checkpoint.Api/appsettings.json` under `Progression`, retaining defaults 3/7/12/19 and initial level 0. Options bind environment overrides and validate strictly increasing positive values at startup. Names/artwork remain in code. No database migration or XP reset.
- Changing settings requires an API restart and can change the current level without increasing the database XP revision; clients must refresh progress after administrative changes. Details and environment variable examples are in README and the frontend contract.
- Full suite passed 31/31 on Mac and 31/31 in Ubuntu ARM64 against isolated PostgreSQL; tests include custom thresholds through actual HTTP and invalid configuration blocking startup. `dotnet format --verify-no-changes` passed.
