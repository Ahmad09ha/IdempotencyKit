import { DynamicModule, Module } from "@nestjs/common";
import { SqliteTransactionalHttpService } from "./sqlite-transactional-http.service.js";
import {
  SQLITE_TRANSACTIONAL_HTTP_OPTIONS,
} from "./sqlite-transactional-http-options.js";
import type {
  SqliteTransactionalHttpOptions,
} from "./sqlite-transactional-http-options.js";

/**
 * Separate from the Standard-mode global interceptor. Transactional endpoints
 * MUST call SqliteTransactionalHttpService.handleExpress directly.
 */
@Module({})
export class SqliteTransactionalHttpModule {
  static forRoot(options: SqliteTransactionalHttpOptions): DynamicModule {
    if (!options || !options.namespace?.trim() || !options.executor ||
        typeof options.resolveCaller !== "function")
      throw new Error("namespace, executor, and trusted resolveCaller are required.");

    const retention = options.retentionMs ?? 86_400_000;
    if (!Number.isSafeInteger(retention) ||
        retention < 1 || retention > 31_536_000_000)
      throw new RangeError("Retention must be between 1 ms and 365 days.");

    return {
      module: SqliteTransactionalHttpModule,
      providers: [
        { provide: SQLITE_TRANSACTIONAL_HTTP_OPTIONS, useValue: options },
        SqliteTransactionalHttpService,
      ],
      exports: [SqliteTransactionalHttpService],
    };
  }
}
