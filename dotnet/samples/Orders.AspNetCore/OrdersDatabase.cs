using Microsoft.Data.Sqlite;

internal sealed class OrdersDatabase(string file)
{
    public async Task InitializeAsync()
    {
        await using var database = CreateConnection(SqliteOpenMode.ReadWrite);
        await database.OpenAsync();
        await using var schema = database.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS orders (
                id INTEGER PRIMARY KEY,
                product_id INTEGER NOT NULL CHECK (product_id > 0),
                quantity INTEGER NOT NULL CHECK (quantity > 0)
            )
            """;
        await schema.ExecuteNonQueryAsync();
    }

    public async Task<long> CountAsync(CancellationToken ct)
    {
        await using var database = CreateConnection(SqliteOpenMode.ReadOnly);
        await database.OpenAsync(ct);
        await using var query = database.CreateCommand();
        query.CommandText = "SELECT count(*) FROM orders";
        return (long)(await query.ExecuteScalarAsync(ct))!;
    }

    public async Task<Order?> GetAsync(long id, CancellationToken ct)
    {
        await using var database = CreateConnection(SqliteOpenMode.ReadOnly);
        await database.OpenAsync(ct);
        await using var query = database.CreateCommand();
        query.CommandText = "SELECT id, product_id, quantity FROM orders WHERE id = $id";
        query.Parameters.AddWithValue("$id", id);
        await using var row = await query.ExecuteReaderAsync(ct);
        return await row.ReadAsync(ct) ? new Order(row.GetInt64(0), row.GetInt32(1), row.GetInt32(2)) : null;
    }

    private SqliteConnection CreateConnection(SqliteOpenMode mode) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = mode,
            Pooling = false
        }.ToString());
}

internal sealed record Order(long Id, int ProductId, int Quantity);
