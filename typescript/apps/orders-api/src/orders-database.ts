import { DatabaseSync } from "node:sqlite";
import { initializeSqliteDatabase } from "@idempotencykit/sqlite";

export interface Order {
  id: number;
  productId: number;
  quantity: number;
}

export class OrdersDatabase {
  constructor(private readonly file: string) {}

  initialize(): void {
    initializeSqliteDatabase(this.file);
    const database = new DatabaseSync(this.file, { timeout: 5_000 });
    try {
      database.exec(`
        CREATE TABLE IF NOT EXISTS orders (
          id INTEGER PRIMARY KEY,
          product_id INTEGER NOT NULL CHECK (product_id > 0),
          quantity INTEGER NOT NULL CHECK (quantity > 0)
        )
      `);
    } finally {
      database.close();
    }
  }

  count(): number {
    return this.read(database => {
      const row = database.prepare("SELECT count(*) AS count FROM orders").get();
      return Number(row?.count);
    });
  }

  get(id: number): Order | undefined {
    return this.read(database => {
      const row = database.prepare(
        "SELECT id, product_id AS productId, quantity FROM orders WHERE id = ?",
      ).get(id);
      return row && {
        id: Number(row.id),
        productId: Number(row.productId),
        quantity: Number(row.quantity),
      };
    });
  }

  private read<T>(query: (database: DatabaseSync) => T): T {
    const database = new DatabaseSync(this.file, { readOnly: true, timeout: 2_000 });
    try {
      return query(database);
    } finally {
      database.close();
    }
  }
}
