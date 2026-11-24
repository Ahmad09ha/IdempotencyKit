import {
  BadRequestException, Controller, Get, NotFoundException, Param, Post, Req, Res,
  UseGuards,
} from "@nestjs/common";
import {
  SqliteTransactionalHttpService, type TransactionalExpressResponse,
} from "@idempotencykit/nestjs-sqlite";
import { DemoAuthGuard, type DemoRequest } from "./demo-auth.guard.js";
import { OrdersDatabase } from "./orders-database.js";

@Controller()
export class AppController {
  constructor(
    private readonly transaction: SqliteTransactionalHttpService,
    private readonly orders: OrdersDatabase,
  ) {}

  @Get("health")
  getHealth() {
    return {
      service: "orders-nestjs",
      status: "ok",
      idempotency: "sqlite-transactional",
    };
  }

  @Get("orders")
  @UseGuards(DemoAuthGuard)
  countOrders() {
    return { count: this.orders.count() };
  }

  @Get("orders/:id")
  @UseGuards(DemoAuthGuard)
  getOrder(@Param("id") value: string) {
    const id = Number(value);
    if (!/^\d+$/.test(value) || !Number.isSafeInteger(id) || id < 1) {
      throw new NotFoundException();
    }
    const order = this.orders.get(id);
    if (!order) throw new NotFoundException();
    return order;
  }

  @Post("orders")
  @UseGuards(DemoAuthGuard)
  async createOrder(
    @Req() request: DemoRequest,
    @Res() response: TransactionalExpressResponse,
  ): Promise<void> {
    // Nest's JSON parser has populated body. Validate domain input before
    // entering the transaction; the adapter still fingerprints rawBody.
    const body = request.body as { productId?: unknown; quantity?: unknown } | undefined;
    const productId = body?.productId;
    const quantity = body?.quantity;
    if (!isPositiveInt(productId) || !isPositiveInt(quantity)) {
      throw new BadRequestException("productId and quantity must be positive integers.");
    }

    await this.transaction.handleExpress(request, response, "orders.create.v1", async session => {
      // Use the library's statement session, never a second connection.
      const result = session.prepare(
        "INSERT INTO orders(product_id, quantity) VALUES (?, ?)",
      ).run(productId, quantity);
      const id = Number(result.lastInsertRowid);
      return {
        statusCode: 201,
        contentType: "application/json",
        body: Buffer.from(JSON.stringify({ id, productId, quantity })),
        headers: { Location: `/orders/${id}` },
      };
    });
  }
}

function isPositiveInt(value: unknown): value is number {
  return typeof value === "number" && Number.isInteger(value) && value > 0 && value <= 2_147_483_647;
}
