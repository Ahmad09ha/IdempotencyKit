/**
 * Actual isolated NestJS Express HTTP server for SQLite process crash tests.
 * The parent sends a real HTTP request, waits for a before/after COMMIT
 * checkpoint, then terminates this whole process with SIGKILL.
 */
import "reflect-metadata";
import { DatabaseSync } from "node:sqlite";
import {
  Controller, Injectable, Module, Post, Req, Res, Inject, UseGuards,
} from "@nestjs/common";
import { NestFactory } from "@nestjs/core";
import { SqliteTransactionalExecutor } from "@idempotencykit/sqlite";
import {
  SqliteTransactionalHttpModule, SqliteTransactionalHttpService,
} from "@idempotencykit/nestjs-sqlite";

const [mode, file, key] = process.argv.slice(2);
if (!["unrelated-writer", "before-commit", "after-commit"].includes(mode) ||
    !process.send || !file || !key)
  throw new Error("SQLite HTTP crash worker requires IPC, mode, file and key.");

const never = () => new Promise(() => { setInterval(() => {}, 1000); });

if (mode === "unrelated-writer") {
  const db = new DatabaseSync(file, { timeout: 100 });
  db.exec("BEGIN IMMEDIATE");
  db.prepare(
    "INSERT INTO orders_sqlite_nest_http_test " +
    "(client_key,tenant_id,user_id,quantity) VALUES (?,?,?,?)",
  ).run("unrelated-" + process.pid, "someone-else", "other", 1);
  process.send({ phase: "locked" });
  await never();
} else {
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

  class CrashHttpController {
    constructor(service) { this.service = service; }

    async create(req, res) {
      return this.service.handleExpress(req, res,
        "orders.sqlite.nest.create.v1",
        async (session, bodyJson, request) => {
          const dto = JSON.parse(bodyJson ?? "{}");
          const row = session.prepare(
            "INSERT INTO orders_sqlite_nest_http_test " +
            "(client_key,tenant_id,user_id,quantity) VALUES (?,?,?,?)",
          ).run(request.headers["idempotency-key"], request.user.tenantId,
            request.user.id, dto.quantity);
          const id = Number(row.lastInsertRowid);
          if (mode === "before-commit") {
            process.send({ phase: "before-commit" });
            await never(); // domain write + claim still uncommitted
          }
          return {
            statusCode: 201, contentType: "application/json",
            body: Buffer.from(JSON.stringify({
              id, quantity: dto.quantity, itemId: dto.itemId,
            })),
            headers: { Location: "/orders/transactional/" + id },
          };
        },
        request => request.user.tenantId !== "revoked");
    }
  }
  Inject(SqliteTransactionalHttpService)(CrashHttpController, undefined, 0);
  Controller("orders/transactional")(CrashHttpController);
  UseGuards(AuthGuard)(CrashHttpController);
  const descriptor = Object.getOwnPropertyDescriptor(CrashHttpController.prototype, "create");
  Post()(CrashHttpController.prototype, "create", descriptor);
  Req()(CrashHttpController.prototype, "create", 0);
  Res()(CrashHttpController.prototype, "create", 1);

  const native = new SqliteTransactionalExecutor(file, 150);
  const executor = {
    async execute(...args) {
      const result = await native.execute(...args);
      if (mode === "after-commit" && result.status === "executed") {
        // SQLite has returned successfully from COMMIT; service has not
        // received the result and has not written any HTTP response bytes.
        process.send({ phase: "after-commit" });
        await never();
      }
      return result;
    },
  };
  class AppModule {}
  Module({
    imports: [SqliteTransactionalHttpModule.forRoot({
      namespace: "sqlite-nest-http-test",
      executor, retentionMs: 86_400_000,
      resolveCaller: req => ({
        principalId: req.user.id, tenantId: req.user.tenantId,
      }),
    })],
    controllers: [CrashHttpController],
  })(AppModule);

  const app = await NestFactory.create(AppModule, { rawBody: true, logger: false });
  await app.listen(0, "127.0.0.1");
  process.send({ phase: "ready", port: app.getHttpServer().address().port });
  await never();
}
