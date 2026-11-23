import { DynamicModule, Module } from "@nestjs/common";
import { PostgresTransactionalHttpService } from "./postgres-transactional-http.service.js";
import {
  POSTGRES_TRANSACTIONAL_HTTP_OPTIONS,
} from "./postgres-transactional-http-options.js";
import type {
  PostgresTransactionalHttpOptions,
} from "./postgres-transactional-http-options.js";

/**
 * Separate from the Standard-mode global interceptor. Transactional endpoints
 * MUST call PostgresTransactionalHttpService.handleExpress directly.
 */
@Module({})
export class PostgresTransactionalHttpModule {
  static forRoot(options: PostgresTransactionalHttpOptions): DynamicModule {
    if (!options || !options.namespace?.trim() || !options.executor ||
        typeof options.resolveCaller !== "function")
      throw new Error("namespace, executor, and trusted resolveCaller are required.");

    const retention = options.retentionMs ?? 86_400_000;
    if (!Number.isSafeInteger(retention) ||
        retention < 1 || retention > 31_536_000_000)
      throw new RangeError("Retention must be between 1 ms and 365 days.");

    return {
      module: PostgresTransactionalHttpModule,
      providers: [
        { provide: POSTGRES_TRANSACTIONAL_HTTP_OPTIONS, useValue: options },
        PostgresTransactionalHttpService,
      ],
      exports: [PostgresTransactionalHttpService],
    };
  }
}
