# Embedded PackForge engine

The unchanged sources in `vendor/` are from DSH-PackForge/dsh-packforge-app,
commit `3d64d2bf96a5d792f05bb34396713c43b9eba09c` (core and host-node 0.1.1).
Both packages declare MIT. The bundled ZIP library is fflate 0.8.2 (MIT).
`bridge.mjs` provides JSON-line progress, argument-safe Node subprocesses and
installation into a new launcher-owned instance. No Electron GUI is bundled.

Rebuild the committed runtime asset with `npm ci && npm run build` in this folder.
Run protocol and rollback tests with `npm test`.

The engine currently imports manifest v4/v5 and .dspack v2/v3. Vendored dependency
payloads from the newer v5 r2 extension are rejected until upstream supports them.
The launcher never passes `force`: replacement of an existing instance is not used.
