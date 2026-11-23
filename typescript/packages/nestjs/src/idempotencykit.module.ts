import { DynamicModule, Module } from "@nestjs/common";
import { APP_INTERCEPTOR } from "@nestjs/core";
import { IDEMPOTENCY_HTTP_OPTIONS } from "./idempotency-options.js";
import type { IdempotencyKitOptions } from "./idempotency-options.js";
import { IdempotencyInterceptor } from "./idempotency.interceptor.js";



/** Provides a global interceptor that acts only on @Idempotent endpoints. */
@Module({})
export class IdempotencyKitModule {
  static forRoot(options: IdempotencyKitOptions): DynamicModule {
    if (!options || !options.namespace?.trim() || !options.store ||
        typeof options.resolveCaller !== "function")
      throw new Error("namespace, store and resolveCaller must be configured.");

    const lease = options.leaseMs ?? 30_000;
    const retention = options.retentionMs ?? 86_400_000;
    if (!Number.isSafeInteger(lease) || lease < 1 || lease > 86_400_000 ||
        !Number.isSafeInteger(retention) || retention < 1 || retention > 31_536_000_000)
      throw new Error("Lease or retention is outside the supported millisecond range.");

    return {
      module: IdempotencyKitModule,
      providers: [
        { provide: IDEMPOTENCY_HTTP_OPTIONS, useValue: options },
        { provide: APP_INTERCEPTOR, useClass: IdempotencyInterceptor },
      ],
      exports: [IDEMPOTENCY_HTTP_OPTIONS],
    };
  }
}
