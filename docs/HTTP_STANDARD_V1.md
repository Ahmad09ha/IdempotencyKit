# Standard-mode HTTP integration v1

**Status: implemented and tested against PostgreSQL 17 in both .NET and NestJS, but not yet published or production-certified.**

This adds HTTP request protection to the existing scoped SHA-256 fingerprint and PostgreSQL Standard-mode store. It **does not** make arbitrary business writes atomic with the idempotency result. See [Guarantees](../spec/GUARANTEES.md) for that crucial distinction.

## What is implemented?

A protected route requires an authenticated caller and one `Idempotency-Key`. On a new key, it attempts an atomic PostgreSQL claim. On a completed matching request, it replays the stored response without calling the handler again. Mismatched fingerprints and in-progress requests fail before the handler runs. A handler exception or unsupported result is treated as **uncertain**; it is not automatically re-executed.

The implementation supports authenticated JSON `POST`, `PUT` and `PATCH` routes, with request bodies up to **64 KiB** and successful 2xx JSON responses up to **256 KiB**. Replayable responses must contain actual JSON: 204/205 (no content) and 206 (range response) are unsupported. Portable replay header values and content type use ASCII printable characters or horizontal tab; percent-encode non-ASCII URI paths. Raw streaming/manual responses, multipart, and arbitrary custom serializers are not supported.

## Before using either framework

1. Apply the shared SQL migration [`001_idempotencykit_records.sql`](../spec/sql/postgres/001_idempotencykit_records.sql) with your application's migration tooling.
2. Install/register your **real authentication and authorization** stack. Build the caller scope from server-verified claims or an authenticated guard, never an arbitrary user header.
3. Configure a PostgreSQL connection pool, an idempotency namespace, a caller scope resolver, a lease duration, and retention.
4. Opt in individual JSON write endpoints by stable operation ID. Clients generate a **new unpredictable key per logical business operation**, and reuse that key on transport retries.
5. Run a job calling the store's expired-completed cleanup API; this integration does not schedule cleanup.
6. Monitor uncertain records and implement application-specific reconciliation. **Never automatically retry unknown business effects.**

Packages can be built and packed locally as preview artifacts; they are not published. npm library manifests still have `private=true`. The snippets below use implemented API names; their user authentication and domain handlers remain application-defined.

## ASP.NET Core 10

Reference `IdempotencyKit.Core`, `IdempotencyKit.Postgres` and `IdempotencyKit.AspNetCore.Standard` (the C# namespace is `IdempotencyKit.AspNetCore`). Register the store as an application service and middleware **after authorization but before routing to endpoint execution**:

~~~csharp
using System.Security.Claims;
using IdempotencyKit.Core;
using IdempotencyKit.Postgres;
using IdempotencyKit.AspNetCore;
using Npgsql;

// Your application must configure its real authentication scheme separately.
builder.Services.AddAuthorization();

builder.Services.AddSingleton(
    NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("Default")!));
builder.Services.AddSingleton<IIdempotencyStore>(sp =>
    new PostgresIdempotencyStore(sp.GetRequiredService<NpgsqlDataSource>()));

builder.Services.AddIdempotencyKit(options =>
{
    options.Namespace = "orders-api";
    options.LeaseDuration = TimeSpan.FromSeconds(30);
    options.Retention = TimeSpan.FromHours(24);
    options.ResolveCaller = ctx => new IdempotencyCallerScope(
        ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("Authenticated principal missing."),
        ctx.User.FindFirst("tenant_id")?.Value);
});

// After builder.Build():
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseIdempotencyKit();

app.MapPost("/orders", (CreateOrderDto dto) =>
    Results.Json(new { orderId = 1001, quantity = dto.Quantity }, statusCode: 201))
   .RequireAuthorization()
   .WithIdempotency("orders.create.v1");
~~~

The example's `CreateOrderDto` and JWT/cookie authentication are defined by the consuming application. There is **no** insecure user-header fallback built into IdempotencyKit.

For an MVC action:

~~~csharp
[Authorize]
[HttpPost]
[Idempotent("orders.create.v1")]
public IActionResult Create([FromBody] CreateOrderDto dto)
    => StatusCode(201, new { orderId = 1001 });
~~~

**Implementation detail:** Unlike our initial design sketch, the ASP.NET Core integration is **endpoint metadata + middleware**, not a pure `AddEndpointFilter`. Minimal API endpoint filters and MVC action filters may run after JSON model binding, which loses original numeric lexemes and may change fingerprint semantics. The middleware reads/buffers the original JSON before binding, then captures the supported result before returning bytes to the client. Both annotations work on the resulting endpoints.

Authentication/authorization must already have run. Protected routes should use `RequireAuthorization` or `[Authorize]` and additionally perform any business-specific ownership checks required before replaying a stored response.

## NestJS 12 (Express adapter)

Reference `@idempotencykit/core`, `@idempotencykit/postgres` and `@idempotencykit/nestjs`. The app must enable raw-body capture to avoid losing exact decimal and big-integer values during JSON parsing:

~~~typescript
const app = await NestFactory.create(AppModule, { rawBody: true });
await app.listen(3000);
~~~

Create the PostgreSQL pool, instantiate the store and register the module. The consuming app owns the PostgreSQL pool lifecycle:

~~~typescript
import { Module } from "@nestjs/common";
import { Pool } from "pg";
import { IdempotencyKitModule } from "@idempotencykit/nestjs";
import { PostgresIdempotencyStore } from "@idempotencykit/postgres";

const pool = new Pool({ connectionString: process.env.DATABASE_URL });
const store = new PostgresIdempotencyStore(pool);

@Module({
  imports: [IdempotencyKitModule.forRoot({
    namespace: "orders-api",
    store,
    leaseMs: 30_000,
    retentionMs: 86_400_000,
    resolveCaller: req => {
      // Only trusted, guard-validated req.user values belong here.
      const user = req.user as { id: string; tenantId?: string };
      return { principalId: user.id, tenantId: user.tenantId };
    },
  })],
})
export class AppModule {}
~~~

Annotate only JSON-returning handlers that are protected by a real auth guard:

~~~typescript
@UseGuards(YourAuthenticationGuard)
@Controller("orders")
export class OrdersController {
  @Post()
  @Idempotent("orders.create.v1")
  create(@Body() dto: CreateOrderDto) {
    return this.orders.create(dto);
  }
}
~~~

Guards run before the global interceptor. Without `rawBody: true`, JSON-body requests fail closed rather than producing fingerprints from potentially rounded JavaScript numbers. The NestJS v1 adapter supports **Express only**. It re-emits stored JSON via Nest's own response pipeline, so do not claim arbitrary byte-for-byte wire parity when other serializers/interceptors modify results. Plain JSON response DTOs are supported; `@Res()`, `StreamableFile`, SSE and custom response transforms are out of scope.

## Client behavior

~~~http
POST /orders HTTP/1.1
Authorization: Bearer <valid-access-token>
Content-Type: application/json
Idempotency-Key: 6ca7449d-a05a-4545-9a60-1ad7750aef9e

{"productId":50,"quantity":2}
~~~

A retry of this **same logical operation** uses the **same** key; placing a different order needs a **new** key. A different body with the same scoped key is a mismatch.

## Observable results

| Condition | Response |
|---|---|
| First eligible request | Run the handler; persist supported JSON 2xx result |
| Completed same-key/same-fingerprint retry | Replay prior supported status/body and safe headers, without handler reexecution |
| Already processing | `409 Conflict` and `Retry-After: 1` |
| Same key but different fingerprint | `422 Unprocessable Entity` |
| Previous outcome uncertain | `409 Conflict`; do not execute handler |
| Missing/invalid required key or JSON | `400 Bad Request` |
| Request exceeds 64 KiB | `413 Payload Too Large` |
| Unsupported request media type | `415 Unsupported Media Type` |
| Storage unavailable | `503 Service Unavailable`, fail closed |
| Handler throws, response unsupported or completion fails | Best-effort mark **uncertain**, return `503` |
| Unauthorized request | Authentication middleware/guard denies before protected handler and replay |

**HTTP error response bodies are not yet fully normalized across frameworks.** The ASP.NET Core implementation uses `application/problem+json`, while NestJS uses its standard exception responses containing stable application error codes.

## Reliability and security boundaries

- **Standard-mode crash window:** a handler may have committed the order even if the idempotency result was not saved. We conservatively retain an uncertain record when possible, but **cannot guarantee exactly one business commit**. Transactional mode is a future milestone.
- **Authorized replay:** authentication/authorization must run on retries, not just the first call. Your code must enforce permission to view the stored result.
- **Leases:** the default 30-second lease must exceed the operation's runtime. Automatic HTTP-level heartbeat is not implemented. An expired owner cannot complete the record. A slow handler's business effects are not fenced.
- **Result scope:** status must be 2xx, JSON only; body ≤256 KiB; safe replay headers are `Location`, `ETag` and `Content-Language`. Unsafe cookies/auth headers are never recorded.
- **Outcomes after validation/handler errors:** an operation that has acquired a key but doesn't produce a supported result becomes uncertain. Choose protected endpoints accordingly; do not expect general 4xx/5xx error replay in v1.
- **External effects:** middleware does not make payments, emails, queues, or external APIs exactly-once.
- **Retention:** database cleanup is explicit. If records are deleted, the same key can be seen as new. Business uniqueness constraints are still necessary.

## Verified test locations

- [.NET HTTP tests](../dotnet/tests/IdempotencyKit.AspNetCore.Tests/HttpStandardTests.cs): Minimal API + MVC, replay, mismatches, auth, in-progress requests, two host instances and uncertain outcomes.
- [NestJS HTTP tests](../typescript/tests/http-standard-v1.test.mjs): actual NestJS Express app, raw body capture, guarded routes, replay, mismatches, identity isolation and uncertain outcomes.
- [PostgreSQL store tests](../spec/POSTGRES_STORE_V1.md): separate store state/lease/cleanup tests.

Both test jobs use PostgreSQL 17 in GitHub Actions. The tests' `X-Test-User` identity injection is a **test-only fake authentication scheme**, not an acceptable production pattern. The Orders sample apps still expose only `GET /health` until configured deliberately with a trusted authentication provider.
