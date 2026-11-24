import { type DynamicModule, Module } from "@nestjs/common";
import { SqliteTransactionalHttpModule } from "@idempotencykit/nestjs-sqlite";
import { SqliteTransactionalExecutor } from "@idempotencykit/sqlite";
import { AppController } from "./app.controller.js";
import { DemoAuthGuard, type DemoRequest } from "./demo-auth.guard.js";
import { OrdersDatabase } from "./orders-database.js";

@Module({})
export class AppModule {
  static configure(file: string): DynamicModule {
    const orders = new OrdersDatabase(file);
    orders.initialize();

    return {
      module: AppModule,
      imports: [
        SqliteTransactionalHttpModule.forRoot({
          namespace: "orders-nestjs",
          retentionMs: 86_400_000,
          executor: new SqliteTransactionalExecutor(file, 250),
          resolveCaller: request => {
            const user = (request as DemoRequest).user;
            if (!user) throw new Error("Authenticated demo principal required.");
            return { principalId: user.id };
          },
        }),
      ],
      controllers: [AppController],
      providers: [DemoAuthGuard, { provide: OrdersDatabase, useValue: orders }],
    };
  }
}
