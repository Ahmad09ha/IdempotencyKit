import { Controller, Get } from "@nestjs/common";

/** A healthy demo host, not an idempotent Orders API yet. */
@Controller()
export class AppController {
  @Get("health")
  getHealth(): { service: string; status: string; idempotency: string } {
    return {
      service: "orders-nestjs",
      status: "ok",
      idempotency: "scaffold-only",
    };
  }
}
