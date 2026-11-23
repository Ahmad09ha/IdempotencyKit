# NestJS / TypeScript workspace (scaffold)

This is an **npm workspace** with three library packages and one NestJS 12 sample application.

- `packages/core` — language-native contracts and state helpers (not a full engine).
- `packages/nestjs` — an **empty** `IdempotencyKitModule`; no interceptor/decorator or protection yet.
- `packages/postgres` — placeholder for a future PostgreSQL store.
- `apps/orders-api` — a real NestJS app exposing **GET /health** only.

**No idempotency middleware, persistence, transactional integration or concurrency-safe processing is implemented.** All packages are marked `private` and `0.0.0` to prevent accidental publishing.

## Prerequisites

- Node.js **24 LTS** recommended. NestJS 12's current CLI generators require Node 22.22.3+ (on the 22.x line) or Node 24.15+; Node 24 LTS on a current patch is preferred.
- npm and an internet connection to install dependencies.
- The workspace uses TypeScript **6** with native ESM (`"type": "module"` and `NodeNext` resolution).

## Quick start

~~~bash
cd typescript
npm install
npm run build
npm test
npm run start:orders
~~~

Visit `http://localhost:3000/health`. The response contains `"idempotency": "scaffold-only"`.

The root `npm run build` command compiles packages in dependency order before compiling the sample. Each library's JavaScript and declarations are emitted under its own `dist/` folder.

> The workspace has no lockfile yet because external npm registry access was unavailable while creating this scaffold. After a successful `npm install`, commit the generated `package-lock.json` for reproducible `npm ci` builds.

## Planned next steps

1. Implement the shared fingerprint and scoped identity contract.
2. Implement a PostgreSQL atomic claim/complete store.
3. Add the NestJS decorator and interceptor.
4. Introduce a transaction-aware operation API and test failures using a real database.
5. Add cross-language parity fixtures and a Docker-based failure lab.

See `../spec/HTTP_CONTRACT.md` and `../docs/ROADMAP.md`.
