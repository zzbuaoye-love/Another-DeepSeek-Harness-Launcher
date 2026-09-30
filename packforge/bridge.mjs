import fs from 'node:fs/promises';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { NodeHost } from './vendor/host-node/src/index.js';
import { inspectPack, installPack, inspectProfile, packProfile, sanitizeSlug } from './vendor/core/src/index.js';

const emit = (type, value) => process.stdout.write(JSON.stringify({ type, ...value }) + '\n');

// The upstream engine owns format semantics. This host keeps dependency commands
// out of cmd.exe and sends their output through the same progress protocol.
class LauncherHost extends NodeHost {
  constructor(pnpmCli) { super(); this.pnpmCli = pnpmCli; }
  async exec(command, args, options = {}) {
    if (command !== 'pnpm' || !this.pnpmCli) throw new Error('未准备好 pnpm 运行环境。');
    return new Promise(resolve => {
      const child = spawn(process.execPath, [this.pnpmCli, ...args], {
        cwd: options.cwd, shell: false, windowsHide: true,
        stdio: ['ignore', 'pipe', 'pipe']
      });
      for (const stream of [child.stdout, child.stderr]) {
        const lines = createInterface({ input: stream });
        lines.on('line', detail => emit('log', { detail: detail.slice(0, 4000) }));
      }
      // The launcher owns this whole process tree and applies its cancellation /
      // timeout at the process boundary, including pnpm's children.
      child.on('error', error => resolve({ status: null, error: error.message }));
      child.on('close', status => resolve({ status }));
    });
  }
}

function validateRelative(name) {
  const normalized = name.replaceAll('\\', '/').replace(/\/$/, '');
  if (!normalized || normalized.startsWith('/') || /[:\x00]/.test(normalized) ||
      normalized.split('/').some(part => !part || part === '..' || part === '.'))
    throw new Error(`包内路径不安全：${name}`);
}

async function readPack(source) {
  const host = new NodeHost();
  const result = await inspectPack(host, source);
  if (!result.valid) throw new Error(result.validation.join('；'));
  if (!((result.containerVersion === 3 && result.manifest.manifestVersion === 5) ||
        (result.containerVersion === 2 && result.manifest.manifestVersion === 4)))
    throw new Error('当前内置引擎支持 .dspack v2/v3 与 manifest v4/v5。');
  for (const entry of [...result.machine, ...result.overrides, ...result.home, ...result.other])
    validateRelative(entry.path);
  const manifest = result.manifest;
  const units = manifest.type === 'dshhome' ? Object.values(manifest.profiles ?? {}) : [manifest];
  if (manifest.vendored || units.some(unit => unit.vendored) || result.other.some(entry => entry.path.startsWith('vendor/')))
    throw new Error('此包使用 vendored 离线依赖扩展，当前内置引擎尚不支持。');
  const profiles = manifest.type === 'dshhome' ? Object.keys(manifest.profiles) : [sanitizeSlug(manifest.profileName || manifest.name)];
  for (const profile of profiles) {
    if (sanitizeSlug(profile) !== profile || !profile) throw new Error(`Profile 名称不可用：${profile}`);
  }
  for (const file of [...(manifest.files ?? []), ...(manifest.skills ?? []).filter(skill => skill.sha256)])
    validateRelative(file.path);
  const localized = value => typeof value === 'string' ? value : value?.zh || value?.en || manifest.name;
  return { name: manifest.name, title: localized(manifest.displayName), version: manifest.version,
    type: manifest.type || 'profile', dshVersion: manifest.dshVersion || '', profiles,
    defaultProfile: manifest.type === 'dshhome' ? manifest.defaultProfile : profiles[0],
    bundleCount: units.reduce((count, unit) => count + (unit.bundles?.length || 0), 0),
    dependencyCount: units.reduce((count, unit) => count + Object.keys(unit.dependencies ?? {}).length, 0),
    fileCount: result.totalEntries, sha256: result.sha256, size: result.size };
}

export async function run(request) {
  switch (request.command) {
    case 'inspect': return readPack(request.source);
    case 'install': {
      const info = await readPack(request.source);
      const home = path.resolve(request.home);
      // The caller supplies a fresh staging home, never an existing user's home.
      try { await fs.stat(home); throw new Error('安装目标已存在，请创建新的独立实例。'); }
      catch (error) { if (error.code !== 'ENOENT') throw error; }
      const result = await installPack(new LauncherHost(request.pnpmCli), {
        source: request.source, home, profilesRoot: path.join(home, 'profiles'),
        installedDshVersions: [request.dshVersion], force: false,
        onProgress: (stage, detail) => emit('progress', { stage, detail })
      });
      return { ...info, installed: result.installed, home };
    }
    case 'inspectProfile': {
      const result = await inspectProfile(new NodeHost(), { name: path.basename(request.source), dir: request.source });
      return { manifest: result.manifest, files: result.files.length, excluded: result.excluded.length };
    }
    case 'export': return packProfile(new NodeHost(), { name: path.basename(request.source), dir: request.source }, {
      out: request.output, dshVersion: request.dshVersion, force: false
    });
    default: throw new Error('未知的整合包操作。');
  }
}

if (process.argv.includes('--stdio')) {
  try {
    let input = '';
    for await (const chunk of process.stdin) {
      input += chunk;
      if (input.length > 256_000) throw new Error('请求过大。');
    }
    const result = await run(JSON.parse(input));
    emit('result', { result });
  } catch (error) {
    emit('error', { detail: error.message });
    process.exitCode = 1;
  }
}
