import { SetMetadata } from "@nestjs/common";

/** Stable operation ID; protected routes require an Idempotency-Key header. */
export const IDEMPOTENT_ROUTE_METADATA = "idempotencykit:http:route:v1";

export interface IdempotentRouteMetadata {
  readonly operationId: string;
}

/**
 * Apply after the app imports IdempotencyKitModule.forRoot(...).
 * Requires authentication guards, rawBody:true, and JSON-only handlers.
 */
export function Idempotent(operationId: string): ClassDecorator & MethodDecorator {
  if (typeof operationId !== "string" || !operationId.trim())
    throw new Error("A stable, nonempty operationId is required.");
  return SetMetadata(IDEMPOTENT_ROUTE_METADATA, { operationId } satisfies IdempotentRouteMetadata);
}
