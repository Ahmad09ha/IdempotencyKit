/**
 * End-to-end NestJS Express Transactional-mode crash worker.
 * Its parent forks this entire HTTP server and kills the process at the
 * signalled transaction checkpoint. Not a production authentication setup.
 */
import "reflect-metadata";
import pg from "pg";
import { Module, Controller, Post, UseGuards, Injectable, Inject, Req, Res } from "@nestjs/common";
import { NestFactory } from "@nestjs/core";
import { PostgresTransactionalExecutor } from "@idempotencykit/postgres";
import {
  PostgresTransactionalHttpModule, PostgresTransactionalHttpService,
} from "@idempotencykit/nestjs-postgres";

const mode = process.argv[2];
if (!["before-commit", "after-commit"].includes(mode) ||
    !process.send || !process.env.IDEMPOTENCYKIT_TEST_POSTGRES_URL)
  throw new Error("This test server must be forked with mode and PostgreSQL URL.");

const pool = new pg.Pool({
  connectionString: process.env.IDEMPOTENCYKIT_TEST_POSTGRES_URL,
  max: 4,
});
pool.on("error", error => {
  process.stderr.write("Pool error: " + error.message + "\n");
});

function holdUntilKilled() {
  return new Promise(resolve => {
    process.once("message", value => {
      if (value === "continue") resolve();
    });
  });
}

class TestAuthGuard {
  canActivate(ctx) {
    const req = ctx.switchToHttp().getRequest();
    if (!req.headers["x-test-user"]) return false;
    req.user = {
      id: req.headers["x-test-user"],
      tenantId: req.headers["x-test-tenant"] ?? "acme",
    };
    return true;
  }
}
Injectable()(TestAuthGuard);

class CrashOrdersController {
  constructor(service) { this.service = service; }

  async create(request, response) {
    return this.service.handleExpress(
      request, response, "orders.nest.transactional.create.v1",
      async (client, bodyJson, req) => {
        const body = JSON.parse(bodyJson ?? "{}");
        const result = await client.query(
          `INSERT INTO idempotencykit_nest_http_orders_test(
             client_key, tenant_id, user_id, quantity)
           VALUES ($1, $2, $3, $4) RETURNING id`,
          [req.headers["idempotency-key"], req.user.tenantId,
            req.user.id, body.quantity]);
        const id = result.rows[0].id;
        if (mode === "before-commit") {
          process.send({ phase: "before-commit" });
          // Hold the PostgreSQL transaction open until the parent SIGKILLs us.
          await holdUntilKilled();
        }
        return {
          statusCode: 201,
          contentType: "application/json",
          body: Buffer.from(JSON.stringify({
            id, quantity: body.quantity, itemId: body.itemId,
          })),
          headers: { Location: "/orders/transactional/" + id },
        };
      },
      req => req.user.tenantId !== "revoked",
    );
  }
}
Inject(PostgresTransactionalHttpService)(CrashOrdersController, undefined, 0);
Controller("orders/transactional")(CrashOrdersController);
UseGuards(TestAuthGuard)(CrashOrdersController);
const descriptor = Object.getOwnPropertyDescriptor(CrashOrdersController.prototype, "create");
Post()(CrashOrdersController.prototype, "create", descriptor);
Req()(CrashOrdersController.prototype, "create", 0);
Res()(CrashOrdersController.prototype, "create", 1);

const baseExecutor = new PostgresTransactionalExecutor(pool, 250);
const executor = {
  async execute(...args) {
    const result = await baseExecutor.execute(...args);
    if (mode === "after-commit" && result.status === "executed") {
      // The transaction has returned from COMMIT, but the HTTP response has
      // NOT been written because the service is awaiting this wrapper.
      process.send({ phase: "after-commit" });
      await holdUntilKilled();
    }
    return result;
  },
};

class TestModule {}
Module({
  imports: [PostgresTransactionalHttpModule.forRoot({
    namespace: "nest-transactional-http-test",
    executor,
    retentionMs: 180_000,
    resolveCaller: req => ({
      principalId: req.user.id, tenantId: req.user.tenantId,
    }),
  })],
  controllers: [CrashOrdersController],
})(TestModule);

const app = await NestFactory.create(TestModule, { rawBody: true, logger: false });
await app.listen(0, "127.0.0.1");
const { port } = app.getHttpServer().address();
process.send({ phase: "ready", port });
