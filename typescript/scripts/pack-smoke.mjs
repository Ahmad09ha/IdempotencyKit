import { equal, ok } from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtemp, readdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const workspace = fileURLToPath(new URL("../", import.meta.url));
const npm = process.env.npm_execpath;
if (!npm) throw new Error("Run with npm run pack:smoke.");
const scratch = await mkdtemp(join(tmpdir(), "idk-npm-consumer-"));
const packages = ["core", "postgres", "sqlserver", "sqlite", "nestjs",
  "nestjs-postgres", "nestjs-sqlserver", "nestjs-sqlite"];
const exportsToCheck = ["computeRequestFingerprint", "PostgresTransactionalExecutor",
  "SqlServerTransactionalExecutor", "SqliteTransactionalExecutor", "IdempotencyKitModule",
  "PostgresTransactionalHttpModule", "SqlServerTransactionalHttpModule", "SqliteTransactionalHttpModule"];

function run(args, cwd = scratch, json = false) {
  const result = spawnSync(process.execPath, args, {
    cwd, encoding: "utf8", stdio: json ? ["ignore", "pipe", "inherit"] : "inherit",
  });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`Command failed: ${args.join(" ")}`);
  return json ? JSON.parse(result.stdout) : undefined;
}

try {
  const archives = [];
  for (const name of packages) {
    const [pack] = run([npm, "pack", "--json", "--workspace", `@idempotencykit/${name}`,
      "--pack-destination", scratch], workspace, true);
    equal(pack.name, `@idempotencykit/${name}`);
    const paths = pack.files.map(({ path }) => path);
    for (const file of ["package.json", "README.md", "dist/index.js", "dist/index.d.ts"]) {
      ok(paths.includes(file), `${pack.name} missing ${file}`);
    }
    ok(paths.every(path => path === "README.md" || path === "package.json" ||
      path === "LICENSE" || path.startsWith("dist/")), `${pack.name} unexpectedly includes source`);
    if (["postgres", "sqlite", "sqlserver"].includes(name)) {
      ok(paths.includes("dist/sql/001_idempotencykit_records.sql"), `${pack.name} missing migration`);
    }
    archives.push(join(scratch, pack.filename));
  }
  const tarballs = (await readdir(scratch)).filter(name => name.endsWith(".tgz"));
  equal(tarballs.length, packages.length);

  await writeFile(join(scratch, "package.json"), JSON.stringify({
    name: "idempotencykit-local-package-smoke", private: true, type: "module",
  }));
  // Explicitly install all unpublished tarballs together so internal dependencies resolve
  // to those archives, never to this source workspace or to a future registry release.
  run([npm, "install", "--ignore-scripts", "--no-audit", "--no-fund", ...archives]);

  const imports = packages.map((name, i) =>
    `import { ${exportsToCheck[i]} } from "@idempotencykit/${name}";`);
  const smoke = `${imports.join("\n")}
import { initializeSqliteDatabase, SqliteIdempotencyStore } from "@idempotencykit/sqlite";
import { computeScopeKey } from "@idempotencykit/core";
import assert from "node:assert/strict";
const publicExports: unknown[] = [${exportsToCheck.join(", ")}];
assert.ok(publicExports.every(value => typeof value === "function"));
initializeSqliteDatabase("smoke.db");
const store = new SqliteIdempotencyStore("smoke.db");
const scope = computeScopeKey({ namespace: "smoke", operationId: "test",
  principalId: "local", clientKey: "smoke-1" });
const fingerprint = computeRequestFingerprint({ method: "POST", operationId: "test",
  path: "/test", bodyJson: "{}" });
const claim = await store.tryClaim(scope, fingerprint, 5000);
assert.equal(claim.status, "acquired");
`;
  await writeFile(join(scratch, "smoke.ts"), smoke);
  run([join(workspace, "node_modules/typescript/bin/tsc"), "--ignoreConfig",
    "--strict", "--module", "NodeNext", "--target", "ES2022", "smoke.ts"]);
  run(["smoke.js"]);
  console.log("8 npm tarballs passed standalone install, type, runtime and SQLite smoke checks.");
} finally {
  await rm(scratch, { recursive: true, force: true });
}
