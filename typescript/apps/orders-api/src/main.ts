import "reflect-metadata";
import { NestFactory } from "@nestjs/core";
import { AppModule } from "./app.module.js";

async function bootstrap(): Promise<void> {
  const file = process.env.ORDERS_DB_PATH ?? "orders-nestjs.db";
  const app = await NestFactory.create(AppModule.configure(file), { rawBody: true });
  app.enableShutdownHooks();
  const port = Number(process.env.PORT ?? "3000");

  // Keep the sample and its intentionally public example token local.
  await app.listen(port, "127.0.0.1");
}

void bootstrap();
