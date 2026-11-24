/**
 * Separate actual NestJS Express HTTP server for before/after SQL Server COMMIT
 * SIGKILL injection. ONLY for real SQL Server integration tests.
 */
import "reflect-metadata";
import sql from "mssql";
import {
  Controller, Injectable, Module, Post, Req, Res, Inject, UseGuards,
} from "@nestjs/common";
import { NestFactory } from "@nestjs/core";
import { SqlServerTransactionalExecutor } from "@idempotencykit/sqlserver";
import {
  SqlServerTransactionalHttpModule, SqlServerTransactionalHttpService,
} from "@idempotencykit/nestjs-sqlserver";

const mode = process.argv[2];
if (!["before-commit", "after-commit"].includes(mode) ||
    !process.send || !process.env.IDEMPOTENCYKIT_TEST_SQLSERVER_PASSWORD)
  throw new Error("Only fork from SQL Server HTTP integration test with mode.");

const pool = await new sql.ConnectionPool({
  server: process.env.IDEMPOTENCYKIT_TEST_SQLSERVER_HOST ?? "127.0.0.1",
  port: Number(process.env.IDEMPOTENCYKIT_TEST_SQLSERVER_PORT ?? "1433"),
  database: "master", user: "sa",
  password: process.env.IDEMPOTENCYKIT_TEST_SQLSERVER_PASSWORD,
  options: { encrypt: true, trustServerCertificate: true },
  pool: { max: 6, min: 0, idleTimeoutMillis: 10_000 },
  connectionTimeout: 5_000, requestTimeout: 30_000,
}).connect();

const holdUntilKilled = () => new Promise(resolve => {
  process.once("message", value => {
    if (value === "continue") resolve();
  });
});

class AuthGuard {
  canActivate(context) {
    const req = context.switchToHttp().getRequest();
    if (!req.headers["x-test-user"]) return false;
    req.user = {
      id: req.headers["x-test-user"],
      tenantId: req.headers["x-test-tenant"] ?? "acme",
    };
    return true;
  }
}
Injectable()(AuthGuard);

class CrashController {
  constructor(service) { this.service = service; }

  async create(req, res) {
    return this.service.handleExpress(req, res,
      "orders.sqlserver.nest.create.v1",
      async (transaction, rawJson, request) => {
        const dto = JSON.parse(rawJson ?? "{}");
        const row = await new sql.Request(transaction)
          .input("key", sql.NVarChar(128), request.headers["idempotency-key"])
          .input("tenant", sql.NVarChar(100), request.user.tenantId)
          .input("principal", sql.NVarChar(100), request.user.id)
          .input("qty", sql.Int, dto.quantity)
          .query(`
            INSERT INTO dbo.idempotencykit_sqlserver_nest_http_orders_test
              (client_key, tenant_id, user_id, quantity)
            OUTPUT INSERTED.id
            VALUES (@key, @tenant, @principal, @qty)`);
        const id = Number(row.recordset[0].id);
        if (mode === "before-commit") {
          process.send({ phase: "before-commit" });
          await holdUntilKilled(); // Uncommitted SQL + claim until SIGKILL.
        }
        return {
          statusCode: 201, contentType: "application/json",
          body: Buffer.from(JSON.stringify({
            id, quantity: dto.quantity, itemId: dto.itemId,
          })),
          headers: { Location: "/orders/transactional/" + id },
        };
      }, r => r.user.tenantId !== "revoked");
  }
}
Inject(SqlServerTransactionalHttpService)(CrashController, undefined, 0);
Controller("orders/transactional")(CrashController);
UseGuards(AuthGuard)(CrashController);
const descriptor = Object.getOwnPropertyDescriptor(CrashController.prototype, "create");
Post()(CrashController.prototype, "create", descriptor);
Req()(CrashController.prototype, "create", 0);
Res()(CrashController.prototype, "create", 1);

const native = new SqlServerTransactionalExecutor(pool, 250);
const executor = {
  async execute(...args) {
    const result = await native.execute(...args);
    if (mode === "after-commit" && result.status === "executed") {
      // SQL Server has acknowledged COMMIT; HTTP response not started yet.
      process.send({ phase: "after-commit" });
      await holdUntilKilled();
    }
    return result;
  },
};

class TestModule {}
Module({
  imports: [SqlServerTransactionalHttpModule.forRoot({
    namespace: "sqlserver-nest-http-test",
    executor, retentionMs: 180_000,
    resolveCaller: req => ({
      principalId: req.user.id, tenantId: req.user.tenantId,
    }),
  })],
  controllers: [CrashController],
})(TestModule);

const app = await NestFactory.create(TestModule, { rawBody: true, logger: false });
await app.listen(0, "127.0.0.1");
process.send({ phase: "ready", port: app.getHttpServer().address().port });
