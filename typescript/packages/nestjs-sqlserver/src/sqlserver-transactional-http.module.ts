import { DynamicModule, Module } from "@nestjs/common";
import { SqlServerTransactionalHttpService } from "./sqlserver-transactional-http.service.js";
import {
  SQLSERVER_TRANSACTIONAL_HTTP_OPTIONS,
} from "./sqlserver-transactional-http-options.js";
import type {
  SqlServerTransactionalHttpOptions,
} from "./sqlserver-transactional-http-options.js";

/**
 * Separate from the Standard-mode global interceptor. Transactional endpoints
 * MUST call SqlServerTransactionalHttpService.handleExpress directly.
 */
@Module({})
export class SqlServerTransactionalHttpModule {
  static forRoot(options: SqlServerTransactionalHttpOptions): DynamicModule {
    if (!options || !options.namespace?.trim() || !options.executor ||
        typeof options.resolveCaller !== "function")
      throw new Error("namespace, executor, and trusted resolveCaller are required.");

    const retention = options.retentionMs ?? 86_400_000;
    if (!Number.isSafeInteger(retention) ||
        retention < 1 || retention > 31_536_000_000)
      throw new RangeError("Retention must be between 1 ms and 365 days.");

    return {
      module: SqlServerTransactionalHttpModule,
      providers: [
        { provide: SQLSERVER_TRANSACTIONAL_HTTP_OPTIONS, useValue: options },
        SqlServerTransactionalHttpService,
      ],
      exports: [SqlServerTransactionalHttpService],
    };
  }
}
