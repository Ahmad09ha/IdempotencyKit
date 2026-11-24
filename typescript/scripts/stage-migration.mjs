import { copyFile, mkdir } from "node:fs/promises";

const provider = process.argv[2];
if (!["postgres", "sqlserver", "sqlite"].includes(provider)) {
  throw new Error("Expected postgres, sqlserver, or sqlite.");
}
const name = "001_idempotencykit_records.sql";
const destination = new URL(`../packages/${provider}/dist/sql/`, import.meta.url);
await mkdir(destination, { recursive: true });
await copyFile(
  new URL(`../../spec/sql/${provider}/${name}`, import.meta.url),
  new URL(name, destination),
);
