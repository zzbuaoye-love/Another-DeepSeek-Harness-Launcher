import { build } from 'esbuild';
await build({
  entryPoints: ['bridge.mjs'], bundle: true, platform: 'node', format: 'esm',
  target: 'node22', outfile: '../Resources/PackForge/engine.mjs',
  banner: { js: '// PackForge core 0.1.1, upstream 3d64d2bf96a5d792f05bb34396713c43b9eba09c. See LICENSE.txt.' }
});
