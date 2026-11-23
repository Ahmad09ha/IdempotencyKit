import { Module } from "@nestjs/common";
import { IdempotencyKitModule } from "@idempotencykit/nestjs";
import { AppController } from "./app.controller.js";

@Module({
  imports: [IdempotencyKitModule],
  controllers: [AppController],
})
export class AppModule {}
