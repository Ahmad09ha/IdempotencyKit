import {
  type CanActivate, type ExecutionContext, Injectable, UnauthorizedException,
} from "@nestjs/common";
import type { IdempotencyHttpRequest } from "@idempotencykit/nestjs";

export interface DemoRequest extends IdempotencyHttpRequest {
  user?: { id: string };
}

// Intentionally public credential for a loopback-only sample. Never copy this
// guard to a deployed app; use real authentication in your own project.
@Injectable()
export class DemoAuthGuard implements CanActivate {
  canActivate(context: ExecutionContext): boolean {
    const request = context.switchToHttp().getRequest<DemoRequest>();
    if (request.headers.authorization !== "Bearer orders-demo") {
      throw new UnauthorizedException("Use the local Orders demo credential.");
    }
    request.user = { id: "orders-demo" };
    return true;
  }
}
