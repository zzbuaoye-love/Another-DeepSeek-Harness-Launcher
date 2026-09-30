// PackForge core 0.1.1, upstream 3d64d2bf96a5d792f05bb34396713c43b9eba09c. See LICENSE.txt.

// bridge.mjs
import fs2 from "node:fs/promises";
import path2 from "node:path";
import { spawn as spawn2 } from "node:child_process";
import { createInterface } from "node:readline";

// vendor/host-node/src/index.js
import fs from "node:fs";
import fsp from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import crypto from "node:crypto";
import http from "node:http";
import https from "node:https";
import { spawn, spawnSync } from "node:child_process";
var NodeHost = class {
  #rootCAs;
  joinPath(...parts) {
    return path.join(...parts);
  }
  resolvePath(...parts) {
    return path.resolve(...parts);
  }
  cwd() {
    return process.cwd();
  }
  homedir() {
    return os.homedir();
  }
  env(name) {
    return process.env[name] ?? null;
  }
  basename(abs) {
    return path.basename(abs);
  }
  async readTextFile(abs) {
    try {
      return await fsp.readFile(abs, "utf8");
    } catch {
      return null;
    }
  }
  async writeTextFile(abs, text) {
    await fsp.mkdir(path.dirname(abs), { recursive: true });
    await fsp.writeFile(abs, text, "utf8");
  }
  async readFile(abs) {
    try {
      return new Uint8Array(await fsp.readFile(abs));
    } catch {
      return null;
    }
  }
  async writeFile(abs, data) {
    await fsp.mkdir(path.dirname(abs), { recursive: true });
    await fsp.writeFile(abs, data);
  }
  async stat(abs) {
    try {
      const s = await fsp.stat(abs);
      return {
        size: s.size,
        isFile: s.isFile(),
        isDirectory: s.isDirectory(),
        isSymbolicLink: s.isSymbolicLink()
      };
    } catch {
      return null;
    }
  }
  async readdir(abs) {
    try {
      const entries = await fsp.readdir(abs, { withFileTypes: true });
      return entries.map((e) => ({
        name: e.name,
        abs: path.join(abs, e.name),
        type: e.isSymbolicLink() ? "symlink" : e.isDirectory() ? "dir" : e.isFile() ? "file" : "other"
      }));
    } catch {
      return null;
    }
  }
  async mkdir(abs) {
    await fsp.mkdir(abs, { recursive: true });
  }
  async rm(abs, opts = {}) {
    await fsp.rm(abs, { recursive: opts.recursive !== false, force: opts.force !== false });
  }
  async mkdtemp(prefix) {
    return await fsp.mkdtemp(path.join(os.tmpdir(), prefix));
  }
  async sha256(data) {
    return crypto.createHash("sha256").update(data).digest("hex");
  }
  async sha256File(abs) {
    try {
      return await new Promise((resolve, reject) => {
        const hash = crypto.createHash("sha256");
        const stream = fs.createReadStream(abs);
        stream.on("data", (chunk) => hash.update(chunk));
        stream.on("end", () => resolve(hash.digest("hex")));
        stream.on("error", reject);
      });
    } catch {
      return null;
    }
  }
  async exec(cmd, args, opts = {}) {
    return await new Promise((resolve) => {
      let child;
      try {
        child = spawn(cmd, args, {
          cwd: opts.cwd,
          stdio: ["ignore", "inherit", "inherit"],
          shell: process.platform === "win32",
          windowsHide: true
        });
      } catch (err2) {
        return resolve({ status: null, error: err2.message });
      }
      let settled = false;
      let timer = null;
      const finish = (status, error) => {
        if (settled) return;
        settled = true;
        if (timer) clearTimeout(timer);
        resolve({ status, error });
      };
      if (opts.timeoutMs > 0) {
        timer = setTimeout(() => {
          if (child.pid) {
            if (process.platform === "win32") {
              try {
                spawnSync("taskkill", ["/pid", String(child.pid), "/T", "/F"], { windowsHide: true });
              } catch {
              }
            } else {
              try {
                process.kill(child.pid, "SIGTERM");
              } catch {
              }
            }
          }
          finish(null, `\u547D\u4EE4\u8D85\u65F6\uFF08${opts.timeoutMs}ms\uFF09\uFF1A${cmd} ${(args ?? []).join(" ")}`);
        }, opts.timeoutMs);
      }
      child.on("error", (err2) => finish(null, err2.message));
      child.on("close", (code) => finish(code ?? 0, void 0));
    });
  }
  async download(url, destAbs) {
    try {
      await this.#downloadOnce(url, destAbs, null);
    } catch (e) {
      if (this.#isCertError(e)) {
        const cas = await this.#systemRootCAs();
        if (cas && cas.length) {
          await this.#downloadOnce(url, destAbs, cas);
          return;
        }
      }
      throw e;
    }
  }
  async #downloadOnce(url, destAbs, extraCa) {
    await new Promise((resolve, reject) => {
      let u;
      try {
        u = new URL(url);
      } catch {
        return reject(new Error(`\u65E0\u6548\u7684\u4E0B\u8F7D\u5730\u5740\uFF1A${url}`));
      }
      const lib = u.protocol === "https:" ? https : u.protocol === "http:" ? http : null;
      if (!lib) return reject(new Error(`\u4EC5\u652F\u6301 http/https\uFF1A${url}`));
      const options = { headers: { "user-agent": "dspack/0.1.0" } };
      if (extraCa) options.ca = extraCa;
      const req = lib.get(url, options, (res) => {
        if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
          res.resume();
          return resolve(this.#downloadOnce(new URL(res.headers.location, u).href, destAbs, extraCa));
        }
        if (res.statusCode !== 200) {
          res.resume();
          return reject(new Error(`\u4E0B\u8F7D\u5931\u8D25\uFF1AHTTP ${res.statusCode}`));
        }
        const out = fs.createWriteStream(destAbs);
        res.pipe(out);
        out.on("finish", () => out.close(() => resolve()));
        out.on("error", reject);
      });
      req.on("error", reject);
      req.setTimeout(3e4, () => req.destroy(new Error("\u4E0B\u8F7D\u8D85\u65F6\uFF0830s\uFF09")));
    });
  }
  #isCertError(e) {
    const m = String(e?.code ?? "") + " " + String(e?.message ?? "");
    return /UNABLE_TO_VERIFY|SELF_SIGNED|CERT_HAS_EXPIRED|UNABLE_TO_GET_ISSUER|verify the first certificate|ERR_TLS_CERT/i.test(m);
  }
  async #systemRootCAs() {
    if (this.#rootCAs !== void 0) return this.#rootCAs;
    this.#rootCAs = null;
    if (process.platform !== "win32") return this.#rootCAs;
    try {
      const script = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; Get-ChildItem Cert:\\LocalMachine\\Root, Cert:\\CurrentUser\\Root | ForEach-Object { '-----BEGIN CERTIFICATE-----'; [System.Convert]::ToBase64String($_.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert), 'InsertLineBreaks'); '-----END CERTIFICATE-----' }";
      const r = spawnSync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], {
        encoding: "utf8",
        timeout: 15e3,
        windowsHide: true,
        maxBuffer: 16 * 1024 * 1024
      });
      const cas = [...String(r.stdout ?? "").matchAll(/-----BEGIN CERTIFICATE-----[\s\S]*?-----END CERTIFICATE-----/g)].map((m) => m[0]);
      if (cas.length) this.#rootCAs = cas;
    } catch {
      this.#rootCAs = null;
    }
    return this.#rootCAs;
  }
  async move(from, to) {
    await fsp.mkdir(path.dirname(to), { recursive: true });
    await fsp.rename(from, to);
  }
};

// vendor/core/src/security.js
var DENY_EXACT = /* @__PURE__ */ new Set([
  // 依赖与构建产物
  "node_modules",
  "dist",
  "build",
  "coverage",
  ".cache",
  ".turbo",
  ".pnpm-store",
  // 自动生成的 DSH 配置文件（安装时由 bundles + patch 重新合成）
  "cordis.yml",
  // 打包产物（防嵌套）
  "manifest.json",
  // 其他包管理器的锁文件（DSH 使用 pnpm）
  "package-lock.json",
  "yarn.lock",
  // 日志
  "npm-debug.log",
  "pnpm-debug.log",
  // 敏感文件（精确名）
  ".env",
  ".npmrc",
  ".netrc",
  ".yarnrc",
  ".yarnrc.yml",
  ".pypirc",
  ".npmignore",
  // DSH_HOME 级：凭据 / 运行时状态 / 全局设置（dshhome 快照默认排除）
  ".credentials.yaml",
  ".anonymous-user-id",
  "settings.yaml",
  // 导出工作区配置快照（本工具写入，不进包）
  ".dshpkcfg"
]);
var DENY_EXT = /* @__PURE__ */ new Set([".key", ".pem", ".p12", ".pfx", ".crt", ".der", ".asc"]);
var DENY_BASENAME = [
  /^\.env(\..+)?$/,
  // .env / .env.local / .env.production ...
  /(^|\.)credentials?\.ya?ml$/i,
  // credentials.yaml / .credentials.yml / my.credentials.yml
  /\.(credential|credentials)$/i,
  // *.credential / *.credentials
  /^id_(rsa|ed25519|ecdsa|ed448|dsa)(\.pub)?$/,
  // SSH 私钥
  /^secrets?\.(json|ya?ml)$/i,
  // secrets.json / secret.yml
  /^(api[-_]?key|apikey|token)s?([._-].*)?$/i
  // api_key.txt / token.json / api.key
];
var DENY_PATH = [/\.tgz$/, /\.tar\.gz$/, /\.zip$/, /\.dspack$/];
var DENY_PATH_PREFIX = [
  "attachments/",
  // 附件（运行时数据）
  "profiles/web/",
  // 安装基线 profile 模板（PROFILE_TEMPLATES）
  "profiles/headless/",
  "skills/.system/"
  // 安装内部系统技能（skipSystem）
];
function matchExt(name) {
  const idx = name.lastIndexOf(".");
  if (idx <= 0) return "";
  return name.slice(idx).toLowerCase();
}
function isExcluded(relPath) {
  const rel = relPath.replace(/\\/g, "/");
  const segments = rel.split("/");
  const name = segments[segments.length - 1];
  for (const seg of segments) {
    if (DENY_EXACT.has(seg)) return true;
  }
  if (DENY_EXT.has(matchExt(name))) return true;
  for (const re of DENY_BASENAME) {
    if (re.test(name)) return true;
  }
  for (const re of DENY_PATH) {
    if (re.test(rel)) return true;
  }
  for (const p of DENY_PATH_PREFIX) {
    if (rel.startsWith(p)) return true;
  }
  return false;
}

// vendor/core/src/scan.js
async function scanProfile(host, profileDir) {
  const files = [];
  const excluded = [];
  await walk(host, profileDir, "", files, excluded);
  files.sort((a, b) => a.rel.localeCompare(b.rel));
  return { files, excluded };
}
function selectFiles(files, include) {
  if (include === void 0 || include === null) return files;
  const set = include instanceof Set ? include : new Set(include);
  return files.filter((f) => set.has(f.rel));
}
async function walk(host, dir, rel, files, excluded) {
  let entries;
  try {
    entries = await host.readdir(dir);
  } catch {
    return;
  }
  if (!Array.isArray(entries)) return;
  for (const entry of entries) {
    if (!entry || typeof entry.name !== "string") continue;
    const relPath = rel ? `${rel}/${entry.name}` : entry.name;
    if (entry.type === "symlink") {
      excluded.push({ rel: relPath, abs: entry.abs, reason: "symlink" });
      continue;
    }
    if (entry.type === "dir") {
      if (isExcluded(relPath)) {
        excluded.push({ rel: relPath, abs: entry.abs, reason: "deny" });
        continue;
      }
      await walk(host, entry.abs, relPath, files, excluded);
    } else if (entry.type === "file") {
      if (isExcluded(relPath)) {
        excluded.push({ rel: relPath, abs: entry.abs, reason: "deny" });
        continue;
      }
      let size = 0;
      try {
        const st = await host.stat(entry.abs);
        size = st && typeof st.size === "number" ? st.size : 0;
      } catch {
      }
      files.push({ rel: relPath, abs: entry.abs, size });
    }
  }
}

// vendor/core/src/special.js
var IMG_EXT = /\.(png|jpe?g|webp|ico|svg)$/i;
function summarizeSpecial(files) {
  const skills = [];
  const agentPresets = [];
  const icons = [];
  for (const f of files ?? []) {
    const rel = String(f.rel || "").replace(/\\/g, "/");
    if (!rel) continue;
    const seg = rel.split("/");
    if (seg[0] === ".agent-presets") {
      if (seg.length >= 3 && seg[2] === "agent.cordis.yml") {
        agentPresets.push({ id: seg[1], file: rel });
      }
      continue;
    }
    if (seg[0] === "skills") {
      if (seg.length === 2 && seg[1].endsWith(".md")) {
        skills.push({ name: seg[1].slice(0, -3), file: rel });
      } else if (seg.length >= 3 && seg[2] === "SKILL.md") {
        skills.push({ name: seg[1], file: rel });
      }
      continue;
    }
    if (IMG_EXT.test(rel)) {
      if (seg[0] === "icons" || seg[0] === "icon" || seg.length === 1 && /^logo\./i.test(seg[0])) {
        icons.push(rel);
      }
    }
  }
  return { skills, agentPresets, icons };
}

// vendor/core/src/manifest.js
var ICON_PATTERN = /^icons?\/.+\.(png|jpe?g|webp|ico|svg)$/i;
async function buildManifest(host, profile, opts = {}, scan = { files: [] }) {
  const pkg = parseJson(await host.readTextFile(host.joinPath(profile.dir, "package.json")));
  const name = sanitizeSlug(opts.name || profile.name);
  const version = opts.version || pkg?.version || "1.0.0";
  const displayName = opts.displayName || niceName(pkg?.name) || name;
  const description = opts.description ?? pkg?.description ?? "";
  const author = opts.author || (typeof pkg?.author === "string" ? pkg.author : "") || "";
  const dshVersion = opts.dshVersion || "";
  const icon = opts.icon || findIcon(scan.files) || "";
  const patch = await host.readTextFile(host.joinPath(profile.dir, "cordis.patch.yml")) ?? "";
  return {
    manifestVersion: 5,
    type: "profile",
    name,
    version,
    displayName,
    description,
    author,
    icon,
    dshVersion,
    profileName: opts.profileName || profile.name,
    bundles: extractBundles(pkg),
    dependencies: await coordinatesFromProfileDeps(host, profile.dir, pkg?.dependencies),
    patch,
    files: opts.files ?? []
  };
}
function extractBundles(pkg) {
  const bundles = pkg?.dsh?.profile?.bundles;
  if (!Array.isArray(bundles)) return [];
  const seen = /* @__PURE__ */ new Set();
  const out = [];
  for (const b of bundles) {
    if (typeof b === "string" && b.trim() && !seen.has(b)) {
      seen.add(b);
      out.push(b);
    }
  }
  return out;
}
function sanitizeSlug(input) {
  return String(input).trim().toLowerCase().replace(/[^a-z0-9-]+/g, "-").replace(/-+/g, "-").replace(/^-|-$/g, "");
}
function niceName(raw) {
  if (!raw) return "";
  return String(raw).replace(/^dsh-profile-/, "").replace(/^dsh-/, "");
}
function findIcon(files) {
  for (const f of files) {
    const rel = (f.rel || "").replace(/\\/g, "/");
    if (ICON_PATTERN.test(rel)) return rel;
  }
  for (const f of files) {
    const rel = (f.rel || "").replace(/\\/g, "/");
    if (/^logo\.[a-z0-9]+$/i.test(rel)) return rel;
  }
  return "";
}
function parseJson(raw) {
  if (raw == null) return null;
  try {
    return JSON.parse(raw);
  } catch {
    return null;
  }
}
function coordsToPkgDeps(dependencies) {
  const out = {};
  for (const [coord, version] of Object.entries(dependencies ?? {})) {
    const git = parseGitCoord(coord);
    if (git) {
      const ref = version === "latest" ? "" : `#${version}`;
      out[git.name] = `github:${git.owner}/${git.repo}${ref}` + (git.subpath ? `&path:${git.subpath}` : "");
    } else {
      out[coord] = version;
    }
  }
  return out;
}
function parseGitCoord(coord) {
  if (typeof coord !== "string" || !coord.startsWith("github:")) return null;
  const rest = coord.slice("github:".length);
  const hash = rest.indexOf("#path:/");
  const repoPart = hash >= 0 ? rest.slice(0, hash) : rest;
  const subpath = hash >= 0 ? rest.slice(hash + "#path:/".length) : "";
  const slash = repoPart.indexOf("/");
  if (slash <= 0) return null;
  return { owner: repoPart.slice(0, slash), repo: repoPart.slice(slash + 1), subpath, name: subpath || repoPart.slice(slash + 1) };
}
var EXACT_SEMVER = /^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$/;
function isExactSemver(v) {
  return typeof v === "string" && EXACT_SEMVER.test(v.trim());
}
async function coordinatesFromProfileDeps(host, dir, deps) {
  if (!deps || typeof deps !== "object" || Array.isArray(deps)) return {};
  const lockText = await host.readTextFile(host.joinPath(dir, "pnpm-lock.yaml"));
  const out = {};
  for (const [pkgName, spec] of Object.entries(deps)) {
    const git = parsePkgGitSpec(spec);
    if (git) {
      const sha = git.sha || gitCommitFromLock(lockText, pkgName);
      out[`github:${git.owner}/${git.repo}${git.subpath ? `#path:/${git.subpath}` : ""}`] = sha || "latest";
      continue;
    }
    if (typeof spec === "string" && spec && !isExactSemver(spec) && !spec.includes(":")) {
      const v = await readInstalledVersion(host, host.joinPath(dir, "node_modules", pkgName, "package.json"));
      out[pkgName] = v ?? spec;
    } else {
      out[pkgName] = spec;
    }
  }
  return out;
}
function gitCommitFromLock(text, pkgName) {
  if (!text || !pkgName) return null;
  const lines = String(text).split(/\r?\n/);
  let inPackages = false;
  let active = -1;
  for (const line of lines) {
    const trimmed = line.trim();
    if (!trimmed) continue;
    if (!inPackages) {
      if (/^packages:\s*$/.test(trimmed)) inPackages = true;
      continue;
    }
    const indent = line.length - line.trimStart().length;
    if (trimmed.endsWith(":") && indent === 0) {
      if (/^packages:\s*$/.test(trimmed)) {
        active = -1;
        continue;
      }
      break;
    }
    const key = line.match(/^\s*([^:]+):\s*$/);
    if (key) {
      const k = key[1].replace(/^["']|["']$/g, "");
      if (k === pkgName || k.startsWith(`${pkgName}@`)) {
        active = indent;
      } else if (active >= 0 && indent <= active) {
        active = -1;
      }
      continue;
    }
    if (active >= 0) {
      const cm = line.match(/\bcommit:\s*['"]?([0-9a-f]{40})['"]?/);
      if (cm) return cm[1];
    }
  }
  return null;
}
async function readInstalledVersion(host, pkgPath) {
  const pkg = parseJson(await host.readTextFile(pkgPath));
  return typeof pkg?.version === "string" ? pkg.version : null;
}
function parsePkgGitSpec(spec) {
  if (typeof spec !== "string") return null;
  let body = spec;
  if (body.startsWith("github:")) {
    body = body.slice("github:".length);
    let subpath = "";
    const amp = body.indexOf("&path:");
    if (amp >= 0) {
      subpath = body.slice(amp + "&path:".length);
      body = body.slice(0, amp);
    }
    const hash = body.indexOf("#");
    const repoPart = hash >= 0 ? body.slice(0, hash) : body;
    const sha = hash >= 0 ? body.slice(hash + 1) : "";
    const slash = repoPart.indexOf("/");
    if (slash <= 0) return null;
    return { owner: repoPart.slice(0, slash), repo: repoPart.slice(slash + 1), subpath, sha };
  }
  const m = spec.match(
    /^(?:git\+)?https?:\/\/(?:www\.)?github\.com\/([^/#]+)\/([^/#]+?)(?:\.git)?(?:#([^&]+))?(?:&path:([^#]+))?$/
  );
  if (m) {
    return { owner: m[1], repo: m[2], subpath: m[4] || "", sha: m[3] || "" };
  }
  return null;
}
function validateManifest(m) {
  const errors = [];
  if (!m || typeof m !== "object" || Array.isArray(m)) return ["manifest.json \u7F3A\u5931\u6216\u4E0D\u662F\u5BF9\u8C61"];
  if (m.manifestVersion !== 4 && m.manifestVersion !== 5) {
    if (m.manifestVersion === 3 || m.manifestVersion === 2) {
      errors.push(`manifestVersion \u4E3A ${m.manifestVersion}\uFF08\u65E7\u7248 .tgz \u683C\u5F0F\uFF09\uFF0C\u672C\u5DE5\u5177\u4EC5\u5B89\u88C5 v4/v5(.dspack) \u6574\u5408\u5305`);
    } else {
      errors.push("manifestVersion \u5FC5\u987B\u4E3A 4 \u6216 5");
    }
  }
  if (typeof m.name !== "string" || !m.name.trim()) errors.push("manifest.name \u7F3A\u5931\u6216\u4E3A\u7A7A");
  if (typeof m.version !== "string" || !m.version.trim()) errors.push("manifest.version \u7F3A\u5931\u6216\u4E3A\u7A7A");
  if (m.patch !== void 0 && typeof m.patch !== "string") errors.push("manifest.patch \u5FC5\u987B\u662F\u5B57\u7B26\u4E32");
  if (m.dshVersion !== void 0 && typeof m.dshVersion !== "string") errors.push("manifest.dshVersion \u5FC5\u987B\u662F\u5B57\u7B26\u4E32");
  for (const f of ["displayName", "description"]) {
    if (m[f] !== void 0 && !isLocaleString(m[f])) errors.push(`manifest.${f} \u5FC5\u987B\u662F\u5B57\u7B26\u4E32\u6216\u591A\u8BED\u8A00\u5BF9\u8C61`);
  }
  if (m.manifestVersion === 5) {
    if (m.type === "dshhome") errors.push(...validateDshHome(m));
    else if (m.type === void 0 || m.type === "profile") errors.push(...validateProfile(m));
    else errors.push('type \u4EC5\u652F\u6301 "profile" \u6216 "dshhome"');
  } else {
    errors.push(...validateProfile(m));
  }
  return errors;
}
function validateProfile(m) {
  const errors = [];
  if (m.type !== void 0 && m.type !== "profile") {
    errors.push('type \u4EC5\u652F\u6301 "profile"\uFF08collection \u4E3A\u9884\u7559\u503C\uFF0C\u6682\u672A\u652F\u6301\uFF09');
  }
  if (!Array.isArray(m.bundles) || m.bundles.some((b) => typeof b !== "string")) {
    errors.push("manifest.bundles \u5FC5\u987B\u662F\u5B57\u7B26\u4E32\u6570\u7EC4");
  }
  if (typeof m.dependencies !== "object" || m.dependencies === null || Array.isArray(m.dependencies)) {
    errors.push("manifest.dependencies \u5FC5\u987B\u662F\u5BF9\u8C61");
  } else {
    for (const [k, v] of Object.entries(m.dependencies)) {
      if (typeof v !== "string" || !v) errors.push(`dependencies[${k}] \u5FC5\u987B\u662F\u300C\u5750\u6807 \u2192 \u56FA\u5B9A\u7248\u672C\u300D\u5B57\u7B26\u4E32`);
    }
  }
  if (m.files !== void 0) errors.push(...validateFiles(m.files));
  return errors;
}
function validateDshHome(m) {
  const errors = [];
  if (m.type !== void 0 && m.type !== "dshhome") {
    errors.push('type \u4EC5\u652F\u6301 "dshhome"\uFF08\u5355 profile \u8BF7\u7528 type:"profile"\uFF09');
  }
  if (typeof m.profiles !== "object" || m.profiles === null || Array.isArray(m.profiles)) {
    errors.push("manifest.profiles \u5FC5\u987B\u662F\u5BF9\u8C61\uFF08name \u2192 ProfileUnit\uFF09");
  } else {
    const names = Object.keys(m.profiles);
    if (names.length === 0) errors.push("manifest.profiles \u81F3\u5C11\u542B 1 \u4E2A profile");
    for (const reserved of ["web", "headless"]) {
      if (names.includes(reserved)) errors.push(`profiles \u4E0D\u5F97\u542B\u5B89\u88C5\u57FA\u7EBF\u6A21\u677F\u300C${reserved}\u300D`);
    }
    for (const [name, u] of Object.entries(m.profiles)) {
      if (!u || typeof u !== "object" || Array.isArray(u)) {
        errors.push(`profiles[${name}] \u5FC5\u987B\u662F\u5BF9\u8C61`);
        continue;
      }
      if (!Array.isArray(u.bundles) || u.bundles.some((b) => typeof b !== "string")) {
        errors.push(`profiles[${name}].bundles \u5FC5\u987B\u662F\u5B57\u7B26\u4E32\u6570\u7EC4`);
      }
      if (typeof u.dependencies !== "object" || u.dependencies === null || Array.isArray(u.dependencies)) {
        errors.push(`profiles[${name}].dependencies \u5FC5\u987B\u662F\u5BF9\u8C61`);
      } else {
        for (const [k, v] of Object.entries(u.dependencies)) {
          if (typeof v !== "string" || !v) errors.push(`profiles[${name}].dependencies[${k}] \u5FC5\u987B\u662F\u300C\u5750\u6807 \u2192 \u56FA\u5B9A\u7248\u672C\u300D\u5B57\u7B26\u4E32`);
        }
      }
      if (u.patch !== void 0 && typeof u.patch !== "string") errors.push(`profiles[${name}].patch \u5FC5\u987B\u662F\u5B57\u7B26\u4E32`);
    }
    if (typeof m.defaultProfile !== "string" || !m.defaultProfile) {
      errors.push("manifest.defaultProfile \u7F3A\u5931\u6216\u4E3A\u7A7A");
    } else if (!names.includes(m.defaultProfile)) {
      errors.push(`defaultProfile\u300C${m.defaultProfile}\u300D\u4E0D\u5728 profiles \u4E2D`);
    }
  }
  if (m.presets !== void 0) {
    if (typeof m.presets !== "object" || m.presets === null || Array.isArray(m.presets)) {
      errors.push("manifest.presets \u5FC5\u987B\u662F\u5BF9\u8C61\uFF08name \u2192 PresetUnit\uFF09");
    } else {
      for (const [name, u] of Object.entries(m.presets)) {
        if (!u || typeof u !== "object" || Array.isArray(u) || typeof u.path !== "string" || !u.path) {
          errors.push(`presets[${name}] \u5FC5\u987B\u662F\u542B path \u7684\u5BF9\u8C61`);
        }
      }
    }
  }
  if (m.skills !== void 0) {
    if (!Array.isArray(m.skills)) {
      errors.push("manifest.skills \u5FC5\u987B\u662F\u6570\u7EC4");
    } else {
      m.skills.forEach((s, i) => {
        if (!s || typeof s !== "object" || Array.isArray(s) || typeof s.path !== "string" || !s.path) {
          errors.push(`skills[${i}] \u5FC5\u987B\u662F\u542B path \u7684\u5BF9\u8C61`);
        }
      });
    }
  }
  if (m.instructions !== void 0 && (typeof m.instructions !== "string" || !m.instructions)) {
    errors.push("manifest.instructions \u5FC5\u987B\u662F\u975E\u7A7A\u5B57\u7B26\u4E32");
  }
  if (m.files !== void 0) errors.push(...validateFiles(m.files));
  return errors;
}
function validateFiles(files) {
  const errors = [];
  if (!Array.isArray(files)) {
    errors.push("manifest.files \u5FC5\u987B\u662F\u6570\u7EC4");
  } else {
    files.forEach((f, i) => {
      for (const e of validateFileEntry(f)) errors.push(`files[${i}] ${e}`);
    });
  }
  return errors;
}
function validateFileEntry(f) {
  const errors = [];
  if (!f || typeof f !== "object" || Array.isArray(f)) return ["\u4E0D\u662F\u5BF9\u8C61"];
  if (typeof f.path !== "string" || !f.path || f.path.startsWith("/") || /^[a-zA-Z]:/.test(f.path)) {
    errors.push('path \u5FC5\u987B\u662F\u76F8\u5BF9\u8DEF\u5F84\uFF08"+"\u5206\u9694\uFF0C\u4E0D\u4EE5\u76D8\u7B26/\u659C\u6760\u5F00\u5934\uFF09');
  }
  if (typeof f.sha256 !== "string" || !/^[0-9a-f]{64}$/.test(f.sha256)) {
    errors.push("sha256 \u5FC5\u987B\u662F 64 \u4F4D\u5341\u516D\u8FDB\u5236");
  }
  if (typeof f.size !== "number" || !Number.isInteger(f.size) || f.size <= 0) {
    errors.push("size \u5FC5\u987B\u662F\u6B63\u6574\u6570");
  }
  if (!Array.isArray(f.urls) || f.urls.length === 0 || f.urls.some((u) => typeof u !== "string" || !/^https?:\/\//i.test(u))) {
    errors.push("urls \u5FC5\u987B\u662F\u975E\u7A7A\u6570\u7EC4\uFF0C\u4E14\u6BCF\u9879\u662F http(s) \u5730\u5740");
  }
  return errors;
}
function isLocaleString(v) {
  if (typeof v === "string") return true;
  if (v && typeof v === "object" && !Array.isArray(v)) {
    const keys = Object.keys(v);
    return keys.length > 0 && keys.every((k) => typeof k === "string" && k !== "" && typeof v[k] === "string");
  }
  return false;
}

// vendor/core/src/discovery.js
function launcherConfigCandidates(host) {
  const c = [];
  const appData = host.env("APPDATA");
  if (appData) c.push(host.joinPath(appData, "in.dsh-plug.dsh-launcher", "config.json"));
  const xdg = host.env("XDG_CONFIG_HOME") || host.joinPath(host.homedir(), ".config");
  c.push(host.joinPath(xdg, "in.dsh-plug.dsh-launcher", "config.json"));
  c.push(host.joinPath(xdg, "dsh-launcher", "config.json"));
  return c;
}
async function readJson(host, p) {
  const raw = await host.readTextFile(p);
  if (raw == null) return null;
  try {
    return JSON.parse(raw);
  } catch {
    return null;
  }
}
async function findLauncherConfig(host, override) {
  if (override !== void 0) return override || null;
  for (const p of launcherConfigCandidates(host)) {
    if (await host.stat(p) != null) return p;
  }
  return null;
}
async function readLauncherConfig(host, override) {
  const p = await findLauncherConfig(host, override);
  if (!p) return null;
  return await readJson(host, p);
}
async function listInstalledDshVersions(host, opts = {}) {
  const cfg = await readLauncherConfig(host, opts.launcherConfig);
  const versions = (cfg?.versions ?? []).map((v) => v?.version).filter((v) => typeof v === "string" && v);
  return sortVersionsDesc(versions);
}
function parseVersion(v) {
  const m = String(v).match(/^(\d+)\.(\d+)\.(\d+)(?:-([A-Za-z]+)\.(\d+))?/);
  if (!m) return { num: [0, 0, 0], pre: null };
  return { num: [+m[1], +m[2], +m[3]], pre: m[4] ? +m[5] : null };
}
function compareVersions(a, b) {
  const pa = parseVersion(a);
  const pb = parseVersion(b);
  for (let i = 0; i < 3; i++) {
    if (pa.num[i] !== pb.num[i]) return pa.num[i] - pb.num[i];
  }
  if (pa.pre === null && pb.pre === null) return 0;
  if (pa.pre === null) return 1;
  if (pb.pre === null) return -1;
  return pa.pre - pb.pre;
}
function sortVersionsDesc(versions) {
  return [...versions].sort((a, b) => compareVersions(b, a));
}

// node_modules/fflate/esm/index.mjs
import { createRequire } from "module";
var require2 = createRequire("/");
var Worker;
try {
  Worker = require2("worker_threads").Worker;
} catch (e) {
}
var u8 = Uint8Array;
var u16 = Uint16Array;
var i32 = Int32Array;
var fleb = new u8([
  0,
  0,
  0,
  0,
  0,
  0,
  0,
  0,
  1,
  1,
  1,
  1,
  2,
  2,
  2,
  2,
  3,
  3,
  3,
  3,
  4,
  4,
  4,
  4,
  5,
  5,
  5,
  5,
  0,
  /* unused */
  0,
  0,
  /* impossible */
  0
]);
var fdeb = new u8([
  0,
  0,
  0,
  0,
  1,
  1,
  2,
  2,
  3,
  3,
  4,
  4,
  5,
  5,
  6,
  6,
  7,
  7,
  8,
  8,
  9,
  9,
  10,
  10,
  11,
  11,
  12,
  12,
  13,
  13,
  /* unused */
  0,
  0
]);
var clim = new u8([16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15]);
var freb = function(eb, start) {
  var b = new u16(31);
  for (var i = 0; i < 31; ++i) {
    b[i] = start += 1 << eb[i - 1];
  }
  var r = new i32(b[30]);
  for (var i = 1; i < 30; ++i) {
    for (var j = b[i]; j < b[i + 1]; ++j) {
      r[j] = j - b[i] << 5 | i;
    }
  }
  return { b, r };
};
var _a = freb(fleb, 2);
var fl = _a.b;
var revfl = _a.r;
fl[28] = 258, revfl[258] = 28;
var _b = freb(fdeb, 0);
var fd = _b.b;
var revfd = _b.r;
var rev = new u16(32768);
for (i = 0; i < 32768; ++i) {
  x = (i & 43690) >> 1 | (i & 21845) << 1;
  x = (x & 52428) >> 2 | (x & 13107) << 2;
  x = (x & 61680) >> 4 | (x & 3855) << 4;
  rev[i] = ((x & 65280) >> 8 | (x & 255) << 8) >> 1;
}
var x;
var i;
var hMap = function(cd, mb, r) {
  var s = cd.length;
  var i = 0;
  var l = new u16(mb);
  for (; i < s; ++i) {
    if (cd[i])
      ++l[cd[i] - 1];
  }
  var le = new u16(mb);
  for (i = 1; i < mb; ++i) {
    le[i] = le[i - 1] + l[i - 1] << 1;
  }
  var co;
  if (r) {
    co = new u16(1 << mb);
    var rvb = 15 - mb;
    for (i = 0; i < s; ++i) {
      if (cd[i]) {
        var sv = i << 4 | cd[i];
        var r_1 = mb - cd[i];
        var v = le[cd[i] - 1]++ << r_1;
        for (var m = v | (1 << r_1) - 1; v <= m; ++v) {
          co[rev[v] >> rvb] = sv;
        }
      }
    }
  } else {
    co = new u16(s);
    for (i = 0; i < s; ++i) {
      if (cd[i]) {
        co[i] = rev[le[cd[i] - 1]++] >> 15 - cd[i];
      }
    }
  }
  return co;
};
var flt = new u8(288);
for (i = 0; i < 144; ++i)
  flt[i] = 8;
var i;
for (i = 144; i < 256; ++i)
  flt[i] = 9;
var i;
for (i = 256; i < 280; ++i)
  flt[i] = 7;
var i;
for (i = 280; i < 288; ++i)
  flt[i] = 8;
var i;
var fdt = new u8(32);
for (i = 0; i < 32; ++i)
  fdt[i] = 5;
var i;
var flm = /* @__PURE__ */ hMap(flt, 9, 0);
var flrm = /* @__PURE__ */ hMap(flt, 9, 1);
var fdm = /* @__PURE__ */ hMap(fdt, 5, 0);
var fdrm = /* @__PURE__ */ hMap(fdt, 5, 1);
var max = function(a) {
  var m = a[0];
  for (var i = 1; i < a.length; ++i) {
    if (a[i] > m)
      m = a[i];
  }
  return m;
};
var bits = function(d, p, m) {
  var o = p / 8 | 0;
  return (d[o] | d[o + 1] << 8) >> (p & 7) & m;
};
var bits16 = function(d, p) {
  var o = p / 8 | 0;
  return (d[o] | d[o + 1] << 8 | d[o + 2] << 16) >> (p & 7);
};
var shft = function(p) {
  return (p + 7) / 8 | 0;
};
var slc = function(v, s, e) {
  if (s == null || s < 0)
    s = 0;
  if (e == null || e > v.length)
    e = v.length;
  return new u8(v.subarray(s, e));
};
var ec = [
  "unexpected EOF",
  "invalid block type",
  "invalid length/literal",
  "invalid distance",
  "stream finished",
  "no stream handler",
  ,
  "no callback",
  "invalid UTF-8 data",
  "extra field too long",
  "date not in range 1980-2099",
  "filename too long",
  "stream finishing",
  "invalid zip data"
  // determined by unknown compression method
];
var err = function(ind, msg, nt) {
  var e = new Error(msg || ec[ind]);
  e.code = ind;
  if (Error.captureStackTrace)
    Error.captureStackTrace(e, err);
  if (!nt)
    throw e;
  return e;
};
var inflt = function(dat, st, buf, dict) {
  var sl = dat.length, dl = dict ? dict.length : 0;
  if (!sl || st.f && !st.l)
    return buf || new u8(0);
  var noBuf = !buf;
  var resize = noBuf || st.i != 2;
  var noSt = st.i;
  if (noBuf)
    buf = new u8(sl * 3);
  var cbuf = function(l2) {
    var bl = buf.length;
    if (l2 > bl) {
      var nbuf = new u8(Math.max(bl * 2, l2));
      nbuf.set(buf);
      buf = nbuf;
    }
  };
  var final = st.f || 0, pos = st.p || 0, bt = st.b || 0, lm = st.l, dm = st.d, lbt = st.m, dbt = st.n;
  var tbts = sl * 8;
  do {
    if (!lm) {
      final = bits(dat, pos, 1);
      var type = bits(dat, pos + 1, 3);
      pos += 3;
      if (!type) {
        var s = shft(pos) + 4, l = dat[s - 4] | dat[s - 3] << 8, t = s + l;
        if (t > sl) {
          if (noSt)
            err(0);
          break;
        }
        if (resize)
          cbuf(bt + l);
        buf.set(dat.subarray(s, t), bt);
        st.b = bt += l, st.p = pos = t * 8, st.f = final;
        continue;
      } else if (type == 1)
        lm = flrm, dm = fdrm, lbt = 9, dbt = 5;
      else if (type == 2) {
        var hLit = bits(dat, pos, 31) + 257, hcLen = bits(dat, pos + 10, 15) + 4;
        var tl = hLit + bits(dat, pos + 5, 31) + 1;
        pos += 14;
        var ldt = new u8(tl);
        var clt = new u8(19);
        for (var i = 0; i < hcLen; ++i) {
          clt[clim[i]] = bits(dat, pos + i * 3, 7);
        }
        pos += hcLen * 3;
        var clb = max(clt), clbmsk = (1 << clb) - 1;
        var clm = hMap(clt, clb, 1);
        for (var i = 0; i < tl; ) {
          var r = clm[bits(dat, pos, clbmsk)];
          pos += r & 15;
          var s = r >> 4;
          if (s < 16) {
            ldt[i++] = s;
          } else {
            var c = 0, n = 0;
            if (s == 16)
              n = 3 + bits(dat, pos, 3), pos += 2, c = ldt[i - 1];
            else if (s == 17)
              n = 3 + bits(dat, pos, 7), pos += 3;
            else if (s == 18)
              n = 11 + bits(dat, pos, 127), pos += 7;
            while (n--)
              ldt[i++] = c;
          }
        }
        var lt = ldt.subarray(0, hLit), dt = ldt.subarray(hLit);
        lbt = max(lt);
        dbt = max(dt);
        lm = hMap(lt, lbt, 1);
        dm = hMap(dt, dbt, 1);
      } else
        err(1);
      if (pos > tbts) {
        if (noSt)
          err(0);
        break;
      }
    }
    if (resize)
      cbuf(bt + 131072);
    var lms = (1 << lbt) - 1, dms = (1 << dbt) - 1;
    var lpos = pos;
    for (; ; lpos = pos) {
      var c = lm[bits16(dat, pos) & lms], sym = c >> 4;
      pos += c & 15;
      if (pos > tbts) {
        if (noSt)
          err(0);
        break;
      }
      if (!c)
        err(2);
      if (sym < 256)
        buf[bt++] = sym;
      else if (sym == 256) {
        lpos = pos, lm = null;
        break;
      } else {
        var add = sym - 254;
        if (sym > 264) {
          var i = sym - 257, b = fleb[i];
          add = bits(dat, pos, (1 << b) - 1) + fl[i];
          pos += b;
        }
        var d = dm[bits16(dat, pos) & dms], dsym = d >> 4;
        if (!d)
          err(3);
        pos += d & 15;
        var dt = fd[dsym];
        if (dsym > 3) {
          var b = fdeb[dsym];
          dt += bits16(dat, pos) & (1 << b) - 1, pos += b;
        }
        if (pos > tbts) {
          if (noSt)
            err(0);
          break;
        }
        if (resize)
          cbuf(bt + 131072);
        var end = bt + add;
        if (bt < dt) {
          var shift = dl - dt, dend = Math.min(dt, end);
          if (shift + bt < 0)
            err(3);
          for (; bt < dend; ++bt)
            buf[bt] = dict[shift + bt];
        }
        for (; bt < end; ++bt)
          buf[bt] = buf[bt - dt];
      }
    }
    st.l = lm, st.p = lpos, st.b = bt, st.f = final;
    if (lm)
      final = 1, st.m = lbt, st.d = dm, st.n = dbt;
  } while (!final);
  return bt != buf.length && noBuf ? slc(buf, 0, bt) : buf.subarray(0, bt);
};
var wbits = function(d, p, v) {
  v <<= p & 7;
  var o = p / 8 | 0;
  d[o] |= v;
  d[o + 1] |= v >> 8;
};
var wbits16 = function(d, p, v) {
  v <<= p & 7;
  var o = p / 8 | 0;
  d[o] |= v;
  d[o + 1] |= v >> 8;
  d[o + 2] |= v >> 16;
};
var hTree = function(d, mb) {
  var t = [];
  for (var i = 0; i < d.length; ++i) {
    if (d[i])
      t.push({ s: i, f: d[i] });
  }
  var s = t.length;
  var t2 = t.slice();
  if (!s)
    return { t: et, l: 0 };
  if (s == 1) {
    var v = new u8(t[0].s + 1);
    v[t[0].s] = 1;
    return { t: v, l: 1 };
  }
  t.sort(function(a, b) {
    return a.f - b.f;
  });
  t.push({ s: -1, f: 25001 });
  var l = t[0], r = t[1], i0 = 0, i1 = 1, i2 = 2;
  t[0] = { s: -1, f: l.f + r.f, l, r };
  while (i1 != s - 1) {
    l = t[t[i0].f < t[i2].f ? i0++ : i2++];
    r = t[i0 != i1 && t[i0].f < t[i2].f ? i0++ : i2++];
    t[i1++] = { s: -1, f: l.f + r.f, l, r };
  }
  var maxSym = t2[0].s;
  for (var i = 1; i < s; ++i) {
    if (t2[i].s > maxSym)
      maxSym = t2[i].s;
  }
  var tr = new u16(maxSym + 1);
  var mbt = ln(t[i1 - 1], tr, 0);
  if (mbt > mb) {
    var i = 0, dt = 0;
    var lft = mbt - mb, cst = 1 << lft;
    t2.sort(function(a, b) {
      return tr[b.s] - tr[a.s] || a.f - b.f;
    });
    for (; i < s; ++i) {
      var i2_1 = t2[i].s;
      if (tr[i2_1] > mb) {
        dt += cst - (1 << mbt - tr[i2_1]);
        tr[i2_1] = mb;
      } else
        break;
    }
    dt >>= lft;
    while (dt > 0) {
      var i2_2 = t2[i].s;
      if (tr[i2_2] < mb)
        dt -= 1 << mb - tr[i2_2]++ - 1;
      else
        ++i;
    }
    for (; i >= 0 && dt; --i) {
      var i2_3 = t2[i].s;
      if (tr[i2_3] == mb) {
        --tr[i2_3];
        ++dt;
      }
    }
    mbt = mb;
  }
  return { t: new u8(tr), l: mbt };
};
var ln = function(n, l, d) {
  return n.s == -1 ? Math.max(ln(n.l, l, d + 1), ln(n.r, l, d + 1)) : l[n.s] = d;
};
var lc = function(c) {
  var s = c.length;
  while (s && !c[--s])
    ;
  var cl = new u16(++s);
  var cli = 0, cln = c[0], cls = 1;
  var w = function(v) {
    cl[cli++] = v;
  };
  for (var i = 1; i <= s; ++i) {
    if (c[i] == cln && i != s)
      ++cls;
    else {
      if (!cln && cls > 2) {
        for (; cls > 138; cls -= 138)
          w(32754);
        if (cls > 2) {
          w(cls > 10 ? cls - 11 << 5 | 28690 : cls - 3 << 5 | 12305);
          cls = 0;
        }
      } else if (cls > 3) {
        w(cln), --cls;
        for (; cls > 6; cls -= 6)
          w(8304);
        if (cls > 2)
          w(cls - 3 << 5 | 8208), cls = 0;
      }
      while (cls--)
        w(cln);
      cls = 1;
      cln = c[i];
    }
  }
  return { c: cl.subarray(0, cli), n: s };
};
var clen = function(cf, cl) {
  var l = 0;
  for (var i = 0; i < cl.length; ++i)
    l += cf[i] * cl[i];
  return l;
};
var wfblk = function(out, pos, dat) {
  var s = dat.length;
  var o = shft(pos + 2);
  out[o] = s & 255;
  out[o + 1] = s >> 8;
  out[o + 2] = out[o] ^ 255;
  out[o + 3] = out[o + 1] ^ 255;
  for (var i = 0; i < s; ++i)
    out[o + i + 4] = dat[i];
  return (o + 4 + s) * 8;
};
var wblk = function(dat, out, final, syms, lf, df, eb, li, bs, bl, p) {
  wbits(out, p++, final);
  ++lf[256];
  var _a2 = hTree(lf, 15), dlt = _a2.t, mlb = _a2.l;
  var _b2 = hTree(df, 15), ddt = _b2.t, mdb = _b2.l;
  var _c = lc(dlt), lclt = _c.c, nlc = _c.n;
  var _d = lc(ddt), lcdt = _d.c, ndc = _d.n;
  var lcfreq = new u16(19);
  for (var i = 0; i < lclt.length; ++i)
    ++lcfreq[lclt[i] & 31];
  for (var i = 0; i < lcdt.length; ++i)
    ++lcfreq[lcdt[i] & 31];
  var _e = hTree(lcfreq, 7), lct = _e.t, mlcb = _e.l;
  var nlcc = 19;
  for (; nlcc > 4 && !lct[clim[nlcc - 1]]; --nlcc)
    ;
  var flen = bl + 5 << 3;
  var ftlen = clen(lf, flt) + clen(df, fdt) + eb;
  var dtlen = clen(lf, dlt) + clen(df, ddt) + eb + 14 + 3 * nlcc + clen(lcfreq, lct) + 2 * lcfreq[16] + 3 * lcfreq[17] + 7 * lcfreq[18];
  if (bs >= 0 && flen <= ftlen && flen <= dtlen)
    return wfblk(out, p, dat.subarray(bs, bs + bl));
  var lm, ll, dm, dl;
  wbits(out, p, 1 + (dtlen < ftlen)), p += 2;
  if (dtlen < ftlen) {
    lm = hMap(dlt, mlb, 0), ll = dlt, dm = hMap(ddt, mdb, 0), dl = ddt;
    var llm = hMap(lct, mlcb, 0);
    wbits(out, p, nlc - 257);
    wbits(out, p + 5, ndc - 1);
    wbits(out, p + 10, nlcc - 4);
    p += 14;
    for (var i = 0; i < nlcc; ++i)
      wbits(out, p + 3 * i, lct[clim[i]]);
    p += 3 * nlcc;
    var lcts = [lclt, lcdt];
    for (var it = 0; it < 2; ++it) {
      var clct = lcts[it];
      for (var i = 0; i < clct.length; ++i) {
        var len = clct[i] & 31;
        wbits(out, p, llm[len]), p += lct[len];
        if (len > 15)
          wbits(out, p, clct[i] >> 5 & 127), p += clct[i] >> 12;
      }
    }
  } else {
    lm = flm, ll = flt, dm = fdm, dl = fdt;
  }
  for (var i = 0; i < li; ++i) {
    var sym = syms[i];
    if (sym > 255) {
      var len = sym >> 18 & 31;
      wbits16(out, p, lm[len + 257]), p += ll[len + 257];
      if (len > 7)
        wbits(out, p, sym >> 23 & 31), p += fleb[len];
      var dst = sym & 31;
      wbits16(out, p, dm[dst]), p += dl[dst];
      if (dst > 3)
        wbits16(out, p, sym >> 5 & 8191), p += fdeb[dst];
    } else {
      wbits16(out, p, lm[sym]), p += ll[sym];
    }
  }
  wbits16(out, p, lm[256]);
  return p + ll[256];
};
var deo = /* @__PURE__ */ new i32([65540, 131080, 131088, 131104, 262176, 1048704, 1048832, 2114560, 2117632]);
var et = /* @__PURE__ */ new u8(0);
var dflt = function(dat, lvl, plvl, pre, post, st) {
  var s = st.z || dat.length;
  var o = new u8(pre + s + 5 * (1 + Math.ceil(s / 7e3)) + post);
  var w = o.subarray(pre, o.length - post);
  var lst = st.l;
  var pos = (st.r || 0) & 7;
  if (lvl) {
    if (pos)
      w[0] = st.r >> 3;
    var opt = deo[lvl - 1];
    var n = opt >> 13, c = opt & 8191;
    var msk_1 = (1 << plvl) - 1;
    var prev = st.p || new u16(32768), head = st.h || new u16(msk_1 + 1);
    var bs1_1 = Math.ceil(plvl / 3), bs2_1 = 2 * bs1_1;
    var hsh = function(i2) {
      return (dat[i2] ^ dat[i2 + 1] << bs1_1 ^ dat[i2 + 2] << bs2_1) & msk_1;
    };
    var syms = new i32(25e3);
    var lf = new u16(288), df = new u16(32);
    var lc_1 = 0, eb = 0, i = st.i || 0, li = 0, wi = st.w || 0, bs = 0;
    for (; i + 2 < s; ++i) {
      var hv = hsh(i);
      var imod = i & 32767, pimod = head[hv];
      prev[imod] = pimod;
      head[hv] = imod;
      if (wi <= i) {
        var rem = s - i;
        if ((lc_1 > 7e3 || li > 24576) && (rem > 423 || !lst)) {
          pos = wblk(dat, w, 0, syms, lf, df, eb, li, bs, i - bs, pos);
          li = lc_1 = eb = 0, bs = i;
          for (var j = 0; j < 286; ++j)
            lf[j] = 0;
          for (var j = 0; j < 30; ++j)
            df[j] = 0;
        }
        var l = 2, d = 0, ch_1 = c, dif = imod - pimod & 32767;
        if (rem > 2 && hv == hsh(i - dif)) {
          var maxn = Math.min(n, rem) - 1;
          var maxd = Math.min(32767, i);
          var ml = Math.min(258, rem);
          while (dif <= maxd && --ch_1 && imod != pimod) {
            if (dat[i + l] == dat[i + l - dif]) {
              var nl = 0;
              for (; nl < ml && dat[i + nl] == dat[i + nl - dif]; ++nl)
                ;
              if (nl > l) {
                l = nl, d = dif;
                if (nl > maxn)
                  break;
                var mmd = Math.min(dif, nl - 2);
                var md = 0;
                for (var j = 0; j < mmd; ++j) {
                  var ti = i - dif + j & 32767;
                  var pti = prev[ti];
                  var cd = ti - pti & 32767;
                  if (cd > md)
                    md = cd, pimod = ti;
                }
              }
            }
            imod = pimod, pimod = prev[imod];
            dif += imod - pimod & 32767;
          }
        }
        if (d) {
          syms[li++] = 268435456 | revfl[l] << 18 | revfd[d];
          var lin = revfl[l] & 31, din = revfd[d] & 31;
          eb += fleb[lin] + fdeb[din];
          ++lf[257 + lin];
          ++df[din];
          wi = i + l;
          ++lc_1;
        } else {
          syms[li++] = dat[i];
          ++lf[dat[i]];
        }
      }
    }
    for (i = Math.max(i, wi); i < s; ++i) {
      syms[li++] = dat[i];
      ++lf[dat[i]];
    }
    pos = wblk(dat, w, lst, syms, lf, df, eb, li, bs, i - bs, pos);
    if (!lst) {
      st.r = pos & 7 | w[pos / 8 | 0] << 3;
      pos -= 7;
      st.h = head, st.p = prev, st.i = i, st.w = wi;
    }
  } else {
    for (var i = st.w || 0; i < s + lst; i += 65535) {
      var e = i + 65535;
      if (e >= s) {
        w[pos / 8 | 0] = lst;
        e = s;
      }
      pos = wfblk(w, pos + 1, dat.subarray(i, e));
    }
    st.i = s;
  }
  return slc(o, 0, pre + shft(pos) + post);
};
var crct = /* @__PURE__ */ function() {
  var t = new Int32Array(256);
  for (var i = 0; i < 256; ++i) {
    var c = i, k = 9;
    while (--k)
      c = (c & 1 && -306674912) ^ c >>> 1;
    t[i] = c;
  }
  return t;
}();
var crc = function() {
  var c = -1;
  return {
    p: function(d) {
      var cr = c;
      for (var i = 0; i < d.length; ++i)
        cr = crct[cr & 255 ^ d[i]] ^ cr >>> 8;
      c = cr;
    },
    d: function() {
      return ~c;
    }
  };
};
var dopt = function(dat, opt, pre, post, st) {
  if (!st) {
    st = { l: 1 };
    if (opt.dictionary) {
      var dict = opt.dictionary.subarray(-32768);
      var newDat = new u8(dict.length + dat.length);
      newDat.set(dict);
      newDat.set(dat, dict.length);
      dat = newDat;
      st.w = dict.length;
    }
  }
  return dflt(dat, opt.level == null ? 6 : opt.level, opt.mem == null ? st.l ? Math.ceil(Math.max(8, Math.min(13, Math.log(dat.length))) * 1.5) : 20 : 12 + opt.mem, pre, post, st);
};
var mrg = function(a, b) {
  var o = {};
  for (var k in a)
    o[k] = a[k];
  for (var k in b)
    o[k] = b[k];
  return o;
};
var b2 = function(d, b) {
  return d[b] | d[b + 1] << 8;
};
var b4 = function(d, b) {
  return (d[b] | d[b + 1] << 8 | d[b + 2] << 16 | d[b + 3] << 24) >>> 0;
};
var b8 = function(d, b) {
  return b4(d, b) + b4(d, b + 4) * 4294967296;
};
var wbytes = function(d, b, v) {
  for (; v; ++b)
    d[b] = v, v >>>= 8;
};
function deflateSync(data, opts) {
  return dopt(data, opts || {}, 0, 0);
}
function inflateSync(data, opts) {
  return inflt(data, { i: 2 }, opts && opts.out, opts && opts.dictionary);
}
var fltn = function(d, p, t, o) {
  for (var k in d) {
    var val = d[k], n = p + k, op = o;
    if (Array.isArray(val))
      op = mrg(o, val[1]), val = val[0];
    if (val instanceof u8)
      t[n] = [val, op];
    else {
      t[n += "/"] = [new u8(0), op];
      fltn(val, n, t, o);
    }
  }
};
var te = typeof TextEncoder != "undefined" && /* @__PURE__ */ new TextEncoder();
var td = typeof TextDecoder != "undefined" && /* @__PURE__ */ new TextDecoder();
var tds = 0;
try {
  td.decode(et, { stream: true });
  tds = 1;
} catch (e) {
}
var dutf8 = function(d) {
  for (var r = "", i = 0; ; ) {
    var c = d[i++];
    var eb = (c > 127) + (c > 223) + (c > 239);
    if (i + eb > d.length)
      return { s: r, r: slc(d, i - 1) };
    if (!eb)
      r += String.fromCharCode(c);
    else if (eb == 3) {
      c = ((c & 15) << 18 | (d[i++] & 63) << 12 | (d[i++] & 63) << 6 | d[i++] & 63) - 65536, r += String.fromCharCode(55296 | c >> 10, 56320 | c & 1023);
    } else if (eb & 1)
      r += String.fromCharCode((c & 31) << 6 | d[i++] & 63);
    else
      r += String.fromCharCode((c & 15) << 12 | (d[i++] & 63) << 6 | d[i++] & 63);
  }
};
function strToU8(str, latin1) {
  if (latin1) {
    var ar_1 = new u8(str.length);
    for (var i = 0; i < str.length; ++i)
      ar_1[i] = str.charCodeAt(i);
    return ar_1;
  }
  if (te)
    return te.encode(str);
  var l = str.length;
  var ar = new u8(str.length + (str.length >> 1));
  var ai = 0;
  var w = function(v) {
    ar[ai++] = v;
  };
  for (var i = 0; i < l; ++i) {
    if (ai + 5 > ar.length) {
      var n = new u8(ai + 8 + (l - i << 1));
      n.set(ar);
      ar = n;
    }
    var c = str.charCodeAt(i);
    if (c < 128 || latin1)
      w(c);
    else if (c < 2048)
      w(192 | c >> 6), w(128 | c & 63);
    else if (c > 55295 && c < 57344)
      c = 65536 + (c & 1023 << 10) | str.charCodeAt(++i) & 1023, w(240 | c >> 18), w(128 | c >> 12 & 63), w(128 | c >> 6 & 63), w(128 | c & 63);
    else
      w(224 | c >> 12), w(128 | c >> 6 & 63), w(128 | c & 63);
  }
  return slc(ar, 0, ai);
}
function strFromU8(dat, latin1) {
  if (latin1) {
    var r = "";
    for (var i = 0; i < dat.length; i += 16384)
      r += String.fromCharCode.apply(null, dat.subarray(i, i + 16384));
    return r;
  } else if (td) {
    return td.decode(dat);
  } else {
    var _a2 = dutf8(dat), s = _a2.s, r = _a2.r;
    if (r.length)
      err(8);
    return s;
  }
}
var slzh = function(d, b) {
  return b + 30 + b2(d, b + 26) + b2(d, b + 28);
};
var zh = function(d, b, z) {
  var fnl = b2(d, b + 28), fn = strFromU8(d.subarray(b + 46, b + 46 + fnl), !(b2(d, b + 8) & 2048)), es = b + 46 + fnl, bs = b4(d, b + 20);
  var _a2 = z && bs == 4294967295 ? z64e(d, es) : [bs, b4(d, b + 24), b4(d, b + 42)], sc = _a2[0], su = _a2[1], off = _a2[2];
  return [b2(d, b + 10), sc, su, fn, es + b2(d, b + 30) + b2(d, b + 32), off];
};
var z64e = function(d, b) {
  for (; b2(d, b) != 1; b += 4 + b2(d, b + 2))
    ;
  return [b8(d, b + 12), b8(d, b + 4), b8(d, b + 20)];
};
var exfl = function(ex) {
  var le = 0;
  if (ex) {
    for (var k in ex) {
      var l = ex[k].length;
      if (l > 65535)
        err(9);
      le += l + 4;
    }
  }
  return le;
};
var wzh = function(d, b, f, fn, u, c, ce, co) {
  var fl2 = fn.length, ex = f.extra, col = co && co.length;
  var exl = exfl(ex);
  wbytes(d, b, ce != null ? 33639248 : 67324752), b += 4;
  if (ce != null)
    d[b++] = 20, d[b++] = f.os;
  d[b] = 20, b += 2;
  d[b++] = f.flag << 1 | (c < 0 && 8), d[b++] = u && 8;
  d[b++] = f.compression & 255, d[b++] = f.compression >> 8;
  var dt = new Date(f.mtime == null ? Date.now() : f.mtime), y = dt.getFullYear() - 1980;
  if (y < 0 || y > 119)
    err(10);
  wbytes(d, b, y << 25 | dt.getMonth() + 1 << 21 | dt.getDate() << 16 | dt.getHours() << 11 | dt.getMinutes() << 5 | dt.getSeconds() >> 1), b += 4;
  if (c != -1) {
    wbytes(d, b, f.crc);
    wbytes(d, b + 4, c < 0 ? -c - 2 : c);
    wbytes(d, b + 8, f.size);
  }
  wbytes(d, b + 12, fl2);
  wbytes(d, b + 14, exl), b += 16;
  if (ce != null) {
    wbytes(d, b, col);
    wbytes(d, b + 6, f.attrs);
    wbytes(d, b + 10, ce), b += 14;
  }
  d.set(fn, b);
  b += fl2;
  if (exl) {
    for (var k in ex) {
      var exf = ex[k], l = exf.length;
      wbytes(d, b, +k);
      wbytes(d, b + 2, l);
      d.set(exf, b + 4), b += 4 + l;
    }
  }
  if (col)
    d.set(co, b), b += col;
  return b;
};
var wzf = function(o, b, c, d, e) {
  wbytes(o, b, 101010256);
  wbytes(o, b + 8, c);
  wbytes(o, b + 10, c);
  wbytes(o, b + 12, d);
  wbytes(o, b + 16, e);
};
function zipSync(data, opts) {
  if (!opts)
    opts = {};
  var r = {};
  var files = [];
  fltn(data, "", r, opts);
  var o = 0;
  var tot = 0;
  for (var fn in r) {
    var _a2 = r[fn], file = _a2[0], p = _a2[1];
    var compression = p.level == 0 ? 0 : 8;
    var f = strToU8(fn), s = f.length;
    var com = p.comment, m = com && strToU8(com), ms = m && m.length;
    var exl = exfl(p.extra);
    if (s > 65535)
      err(11);
    var d = compression ? deflateSync(file, p) : file, l = d.length;
    var c = crc();
    c.p(file);
    files.push(mrg(p, {
      size: file.length,
      crc: c.d(),
      c: d,
      f,
      m,
      u: s != fn.length || m && com.length != ms,
      o,
      compression
    }));
    o += 30 + s + exl + l;
    tot += 76 + 2 * (s + exl) + (ms || 0) + l;
  }
  var out = new u8(tot + 22), oe = o, cdl = tot - o;
  for (var i = 0; i < files.length; ++i) {
    var f = files[i];
    wzh(out, f.o, f, f.f, f.u, f.c.length);
    var badd = 30 + f.f.length + exfl(f.extra);
    out.set(f.c, f.o + badd);
    wzh(out, o, f, f.f, f.u, f.c.length, f.o, f.m), o += 16 + badd + (f.m ? f.m.length : 0);
  }
  wzf(out, o, files.length, cdl, oe);
  return out;
}
function unzipSync(data, opts) {
  var files = {};
  var e = data.length - 22;
  for (; b4(data, e) != 101010256; --e) {
    if (!e || data.length - e > 65558)
      err(13);
  }
  ;
  var c = b2(data, e + 8);
  if (!c)
    return {};
  var o = b4(data, e + 16);
  var z = o == 4294967295 || c == 65535;
  if (z) {
    var ze = b4(data, e - 12);
    z = b4(data, ze) == 101075792;
    if (z) {
      c = b4(data, ze + 32);
      o = b4(data, ze + 48);
    }
  }
  var fltr = opts && opts.filter;
  for (var i = 0; i < c; ++i) {
    var _a2 = zh(data, o, z), c_2 = _a2[0], sc = _a2[1], su = _a2[2], fn = _a2[3], no = _a2[4], off = _a2[5], b = slzh(data, off);
    o = no;
    if (!fltr || fltr({
      name: fn,
      size: sc,
      originalSize: su,
      compression: c_2
    })) {
      if (!c_2)
        files[fn] = slc(data, b, b + sc);
      else if (c_2 == 8)
        files[fn] = inflateSync(data.subarray(b, b + sc), { out: new u8(su) });
      else
        err(14, "unknown compression type " + c_2);
    }
  }
  return files;
}

// vendor/core/src/dspack.js
var encoder = new TextEncoder();
var decoder = new TextDecoder();
var DSPACK_FORMAT = "dspack";
var DSPACK_CONTAINER_VERSION = 3;
function dspackMarker(version) {
  return { format: DSPACK_FORMAT, version };
}
function buildDspack(entries) {
  return zipSync(entries);
}
function parseDspack(bytes) {
  if (!(bytes instanceof Uint8Array) || bytes.length === 0) {
    throw new Error("\u4E0D\u662F\u6709\u6548\u7684 .dspack \u6587\u4EF6\uFF08\u7A7A\u5185\u5BB9\uFF09");
  }
  let entries;
  try {
    entries = unzipSync(bytes);
  } catch {
    throw new Error("\u4E0D\u662F\u6709\u6548\u7684 .dspack \u6587\u4EF6\uFF08\u65E0\u6CD5\u6309 ZIP \u89E3\u538B\uFF09");
  }
  return { entries, marker: parseMarker(entries) };
}
function parseMarker(entries) {
  const raw = entries["dspack.json"];
  if (!raw) return null;
  let m;
  try {
    m = JSON.parse(decodeText(raw));
  } catch {
    throw new Error("dspack.json \u4E0D\u662F\u6709\u6548 JSON");
  }
  if (!m || typeof m !== "object" || Array.isArray(m) || m.format !== DSPACK_FORMAT) {
    throw new Error('dspack.json \u4E0D\u662F\u6709\u6548\u7684\u5BB9\u5668\u6807\u8BB0\uFF08format \u5FC5\u987B\u4E3A "dspack"\uFF09');
  }
  return m;
}
function encodeText(s) {
  return encoder.encode(s);
}
function decodeText(u82) {
  return decoder.decode(u82);
}

// vendor/core/src/pack.js
var ROOT_MACHINE = /* @__PURE__ */ new Set(["package.json", "pnpm-workspace.yaml", "pnpm-lock.yaml"]);
function dspackEntryPath(rel) {
  const r = String(rel).replace(/\\/g, "/");
  return ROOT_MACHINE.has(r) ? r : `overrides/${r}`;
}
async function packProfile(host, profile, opts = {}) {
  const scan = await scanProfile(host, profile.dir);
  if (scan.files.length === 0) {
    throw new Error(`Profile\u300C${profile.name}\u300D\u6CA1\u6709\u53EF\u6253\u5305\u7684\u6587\u4EF6\uFF08\u5168\u90E8\u88AB\u8FC7\u6EE4\u6216\u76EE\u5F55\u4E3A\u7A7A\uFF09`);
  }
  const files = selectFiles(scan.files, opts.include);
  if (files.length === 0) {
    throw new Error(`\u6CA1\u6709\u9009\u4E2D\u7684\u6587\u4EF6\uFF08\u8BF7\u81F3\u5C11\u52FE\u9009\u4E00\u4E2A\u6587\u4EF6/\u76EE\u5F55\uFF09`);
  }
  const manifest = await buildManifest(host, profile, opts, scan);
  const homeDir = opts.home || host.joinPath(profile.dir, "..", "..");
  const homeFiles = (await scanProfile(host, homeDir)).files.filter((f) => !f.rel.startsWith("profiles/"));
  const homeSet = opts.homeInclude instanceof Set ? opts.homeInclude : opts.homeInclude ? new Set(opts.homeInclude) : null;
  const selectedHome = homeSet ? homeFiles.filter((f) => homeSet.has(f.rel)) : [];
  const entries = {};
  for (const f of files) {
    const data = await host.readFile(f.abs);
    if (!data) continue;
    entries[dspackEntryPath(f.rel)] = data;
  }
  for (const f of selectedHome) {
    const data = await host.readFile(f.abs);
    if (!data) continue;
    entries[`home/${f.rel}`] = data;
  }
  entries["manifest.json"] = encodeText(JSON.stringify(manifest, null, 2) + "\n");
  entries["dspack.json"] = encodeText(JSON.stringify(dspackMarker(DSPACK_CONTAINER_VERSION)) + "\n");
  const bytes = buildDspack(entries);
  const outDir = opts.out ? host.resolvePath(opts.out) : host.cwd();
  const outPath = host.joinPath(outDir, `${manifest.name}-${manifest.version}.dspack`);
  if (await host.stat(outPath) != null && !opts.force) {
    throw new Error(`\u8F93\u51FA\u6587\u4EF6\u5DF2\u5B58\u5728\uFF1A${outPath}\uFF08\u4F7F\u7528 --force \u8986\u76D6\uFF09`);
  }
  await host.mkdir(outDir);
  await host.writeFile(outPath, bytes);
  return {
    manifest,
    output: outPath,
    sha256: await host.sha256(bytes),
    size: bytes.length,
    included: Object.keys(entries).length,
    excluded: scan.excluded.length
  };
}

// vendor/core/src/inspect.js
async function inspectProfile(host, profile, opts = {}) {
  const scan = await scanProfile(host, profile.dir);
  const files = selectFiles(scan.files, opts.include);
  const manifest = await buildManifest(host, profile, opts, scan);
  const homeDir = opts.home || host.joinPath(profile.dir, "..", "..");
  const homeFiles = (await scanProfile(host, homeDir)).files.filter((f) => !f.rel.startsWith("profiles/"));
  return {
    profile,
    files,
    allFiles: scan.files,
    excluded: scan.excluded,
    manifest,
    special: summarizeSpecial(files),
    homeFiles,
    homeDir
  };
}
async function inspectPack(host, source) {
  const bytes = source instanceof Uint8Array ? source : await host.readFile(host.resolvePath(source));
  if (!bytes) throw new Error("\u65E0\u6CD5\u8BFB\u53D6\u6574\u5408\u5305\u6587\u4EF6");
  const { entries, marker } = parseDspack(bytes);
  let manifest = null;
  let validation;
  if (entries["manifest.json"]) {
    manifest = parseJson2(decodeText(entries["manifest.json"]));
    validation = manifest ? validateManifest(manifest) : ["manifest.json \u65E0\u6CD5\u89E3\u6790"];
  } else {
    validation = ["\u7F3A\u5C11 manifest.json"];
  }
  const machine = [];
  const overrides = [];
  const home = [];
  const other = [];
  for (const [p, data] of Object.entries(entries)) {
    if (p === "manifest.json") continue;
    const size = data?.byteLength ?? data?.length ?? 0;
    const rec = { path: p, size };
    if (p === "dspack.json" || p === "package.json" || p === "pnpm-workspace.yaml" || p === "pnpm-lock.yaml") machine.push(rec);
    else if (p.startsWith("overrides/")) overrides.push(rec);
    else if (p.startsWith("home/")) home.push(rec);
    else other.push(rec);
  }
  const byPath = (a, b) => a.path.localeCompare(b.path);
  machine.sort(byPath);
  overrides.sort(byPath);
  home.sort(byPath);
  other.sort(byPath);
  return {
    sha256: await host.sha256(bytes),
    size: bytes.byteLength,
    containerVersion: marker?.version ?? null,
    valid: validation.length === 0,
    validation,
    manifest,
    machine,
    overrides,
    home,
    other,
    totalEntries: Object.keys(entries).length
  };
}
function parseJson2(raw) {
  if (raw == null || raw === "") return null;
  try {
    return JSON.parse(raw);
  } catch {
    return null;
  }
}

// vendor/core/src/install.js
async function installPack(host, opts = {}) {
  const { source } = opts;
  if (!source) throw new Error("\u8BF7\u6307\u5B9A\u8981\u5B89\u88C5\u7684\u6574\u5408\u5305\uFF08\u672C\u5730 .dspack \u8DEF\u5F84\u6216 URL\uFF09");
  const progress = (stage, detail) => {
    if (typeof opts.onProgress === "function") opts.onProgress(stage, detail);
  };
  const profilesRoot = opts.profilesRoot || host.joinPath(host.homedir(), ".dsh", "profiles");
  progress("download", typeof source === "string" && /^https?:\/\//i.test(source) ? "\u4E0B\u8F7D\u6574\u5408\u5305" : "\u8BFB\u53D6\u6574\u5408\u5305");
  const { path: packPath, tempDir } = await resolvePackSource(host, source);
  try {
    await verifyIntegrity(host, packPath, opts);
    progress("extract", "\u89E3\u6790\u5E76\u6821\u9A8C\u6574\u5408\u5305");
    const bytes = await host.readFile(packPath);
    if (!bytes) throw new Error("\u65E0\u6CD5\u8BFB\u53D6\u6574\u5408\u5305\u6587\u4EF6");
    const { entries } = parseDspack(bytes);
    if (!entries["manifest.json"]) throw new Error("\u6574\u5408\u5305\u7F3A\u5C11 manifest.json\uFF08\u4E0D\u662F\u6709\u6548\u7684 .dspack\uFF09");
    const manifest = parseJson3(decodeText(entries["manifest.json"]));
    const errors = validateManifest(manifest);
    if (errors.length) throw new Error(`\u6574\u5408\u5305\u4E0D\u5408\u6CD5\uFF1A${errors.join("\uFF1B")}`);
    if (manifest.manifestVersion === 5 && manifest.type === "dshhome") {
      return await installDshHome(host, manifest, entries, opts, progress);
    }
    return await installProfile(host, manifest, entries, opts, progress, profilesRoot);
  } finally {
    if (tempDir) await host.rm(tempDir, { recursive: true, force: true }).catch(() => {
    });
  }
}
async function installProfile(host, manifest, entries, opts, progress, profilesRoot) {
  const profileName = sanitizeSlug(opts.name || manifest.profileName || manifest.name);
  if (!profileName) throw new Error("\u65E0\u6CD5\u786E\u5B9A Profile \u540D\u79F0");
  const target = host.joinPath(profilesRoot, profileName);
  if (opts.dryRun) {
    const exists = await host.stat(target) != null;
    return { profileName, dir: target, manifest, dryRun: true, installed: false, reconcile: null, filesDownloaded: 0, exists };
  }
  if (await host.stat(target) != null && !opts.force) {
    throw new Error(`Profile\u300C${profileName}\u300D\u5DF2\u5B58\u5728\uFF1A${target}\uFF08\u4F7F\u7528 --force \u8986\u76D6\uFF09`);
  }
  if (await host.stat(target) != null) {
    await host.rm(target, { recursive: true, force: true });
  }
  await host.mkdir(target);
  try {
    progress("extract", "\u5199\u5165 overrides/ \u4E0E package.json");
    await materializePackage(host, target, manifest, entries);
    await materializeHome(host, host.joinPath(profilesRoot, ".."), entries);
    let installed = false;
    let reconcile = null;
    if (!opts.noInstall) {
      installed = true;
      progress("install", "\u8FD0\u884C pnpm install\uFF08\u4F9D\u8D56\u91CD\u5EFA\uFF0C\u53EF\u80FD\u8F83\u6162\uFF09");
      await pnpmInstall(host, target, opts, !!entries["pnpm-lock.yaml"]);
      reconcile = await reconcileProfile(host, target, manifest);
      if (reconcile.missing.length > 0) {
        throw new Error(
          `\u6574\u5408\u5305\u5C42\u6808\u6709 ${reconcile.missing.length} \u4E2A bundle \u65E0\u6CD5\u89E3\u6790\u4E3A\u8865\u4E01\u5C42\uFF1A${reconcile.missing.join(", ")}\u3002\u8FD9\u4E9B\u5305\u672A\u58F0\u660E dsh.bundle.patch\uFF0C\u5C5E\u4E8E\u65E0\u6548\u7684\u6574\u5408\u5305\u3002`
        );
      }
    }
    const files = manifest.files ?? [];
    if (files.length) progress("files", `\u4E0B\u8F7D ${files.length} \u4E2A files[] \u6761\u76EE`);
    const filesDownloaded = await downloadFiles(host, target, files);
    progress("done", profileName);
    return { profileName, dir: target, manifest, dryRun: false, installed, reconcile, filesDownloaded };
  } catch (e) {
    await host.rm(target, { recursive: true, force: true }).catch(() => {
    });
    throw e;
  }
}
async function installDshHome(host, manifest, entries, opts, progress) {
  await ensureDsh(host, manifest, opts);
  const homeRoot = opts.home ? host.resolvePath(opts.home) : host.joinPath(host.homedir(), ".dsh");
  if (opts.dryRun) {
    const exists = await host.stat(homeRoot) != null;
    return {
      type: "dshhome",
      dir: homeRoot,
      manifest,
      dryRun: true,
      installed: false,
      exists,
      profiles: Object.keys(manifest.profiles),
      defaultProfile: manifest.defaultProfile,
      filesDownloaded: 0
    };
  }
  if (await host.stat(homeRoot) != null && !opts.force) {
    throw new Error(`\u76EE\u6807 DSH_HOME \u5DF2\u5B58\u5728\uFF1A${homeRoot}\uFF08\u4F7F\u7528 --force \u8986\u76D6\uFF09`);
  }
  if (await host.stat(homeRoot) != null) {
    await host.rm(homeRoot, { recursive: true, force: true });
  }
  await host.mkdir(homeRoot);
  try {
    const installed = [];
    for (const [name, unit] of Object.entries(manifest.profiles)) {
      const profileDir = host.joinPath(homeRoot, "profiles", name);
      progress("extract", `\u5199\u5165 profile\u300C${name}\u300D`);
      await materializeProfile(host, profileDir, name, unit, entries);
      if (!opts.noInstall) {
        progress("install", `\u8FD0\u884C pnpm install\uFF08${name}\uFF0C\u53EF\u80FD\u8F83\u6162\uFF09`);
        await pnpmInstall(host, profileDir, opts, !!entries["pnpm-lock.yaml"]);
        const reconcile = await reconcileProfile(host, profileDir, unit);
        if (reconcile.missing.length > 0) {
          throw new Error(
            `profile\u300C${name}\u300D\u5C42\u6808\u6709 ${reconcile.missing.length} \u4E2A bundle \u65E0\u6CD5\u89E3\u6790\u4E3A\u8865\u4E01\u5C42\uFF1A${reconcile.missing.join(", ")}\u3002\u8FD9\u4E9B\u5305\u672A\u58F0\u660E dsh.bundle.patch\u3002`
          );
        }
      }
      installed.push(name);
    }
    progress("extract", "\u5199\u5165 home \u7EA7\u8D44\u6E90\uFF08preset / skill / \u6307\u4EE4 / \u6570\u636E\uFF09");
    await materializeHomeOverrides(host, homeRoot, entries);
    const heavySkills = (manifest.skills ?? []).filter((s) => s.sha256 && s.size).map((s) => ({ path: s.path, sha256: s.sha256, size: s.size, urls: s.urls }));
    const all = [...manifest.files ?? [], ...heavySkills];
    if (all.length) progress("files", `\u4E0B\u8F7D ${all.length} \u4E2A files[]/skills[] \u6761\u76EE`);
    const filesDownloaded = await downloadFiles(host, homeRoot, all);
    progress("done", manifest.name);
    return {
      type: "dshhome",
      dir: homeRoot,
      manifest,
      dryRun: false,
      installed: true,
      profiles: installed,
      defaultProfile: manifest.defaultProfile,
      filesDownloaded
    };
  } catch (e) {
    await host.rm(homeRoot, { recursive: true, force: true }).catch(() => {
    });
    throw e;
  }
}
async function ensureDsh(host, manifest, opts) {
  if (!manifest.dshVersion) return;
  const installed = opts.installedDshVersions ?? await listInstalledDshVersions(host);
  if (!Array.isArray(installed) || !installed.includes(manifest.dshVersion)) {
    const have = Array.isArray(installed) ? installed.join(", ") || "\u65E0" : "\u672A\u77E5";
    throw new Error(`dshhome \u4F9D\u8D56 DSH ${manifest.dshVersion}\uFF0C\u4F46\u672C\u673A\u672A\u5B89\u88C5\uFF08\u5DF2\u88C5\uFF1A${have}\uFF09\u3002\u8BF7\u5148\u7528\u542F\u52A8\u5668\u5B89\u88C5\u8BE5\u7248\u672C\u540E\u518D\u5BFC\u5165\u3002`);
  }
}
async function resolvePackSource(host, source) {
  if (/^https?:\/\//i.test(source)) {
    const tempDir = await host.mkdtemp("dspack-dl-");
    const dest = host.joinPath(tempDir, "pack.dspack");
    await host.download(source, dest);
    return { path: dest, tempDir };
  }
  const local = host.resolvePath(source);
  const st = await host.stat(local);
  if (!st?.isFile) throw new Error(`\u627E\u4E0D\u5230\u6574\u5408\u5305\u6587\u4EF6\uFF1A${local}`);
  return { path: local, tempDir: null };
}
async function verifyIntegrity(host, packPath, opts = {}) {
  if (Number.isInteger(opts.expectedSize) && opts.expectedSize > 0) {
    const st = await host.stat(packPath);
    if (!st || st.size !== opts.expectedSize) {
      throw new Error(`\u5927\u5C0F\u4E0D\u7B26\uFF1A\u671F\u671B ${opts.expectedSize} \u5B57\u8282\uFF0C\u5B9E\u9645 ${st?.size ?? "\u672A\u77E5"}`);
    }
  }
  if (opts.expectedSha256) {
    const actual = await host.sha256File(packPath);
    if (actual !== opts.expectedSha256.toLowerCase()) {
      throw new Error(`sha256 \u6821\u9A8C\u5931\u8D25\uFF08\u53EF\u80FD\u88AB\u7BE1\u6539\uFF09\uFF1A\u671F\u671B ${opts.expectedSha256}\uFF0C\u5B9E\u9645 ${actual}`);
    }
  }
}
async function materializePackage(host, dir, manifest, entries) {
  for (const [entryPath, data] of Object.entries(entries)) {
    if (!entryPath.startsWith("overrides/")) continue;
    const rel = safeRel(entryPath.slice("overrides/".length));
    if (!rel) continue;
    await host.writeFile(host.joinPath(dir, rel), data);
  }
  const base = parseJson3(decodeText(entries["package.json"] || new Uint8Array())) ?? {};
  const pkg = base && typeof base === "object" && !Array.isArray(base) ? base : {};
  pkg.dependencies = coordsToPkgDeps(manifest.dependencies ?? {});
  pkg.dsh = { ...pkg.dsh ?? {}, profile: { ...pkg.dsh?.profile ?? {}, bundles: manifest.bundles ?? [] } };
  await host.writeTextFile(host.joinPath(dir, "package.json"), JSON.stringify(pkg, null, 2) + "\n");
  for (const name of ["pnpm-workspace.yaml", "pnpm-lock.yaml"]) {
    if (entries[name]) await host.writeFile(host.joinPath(dir, name), entries[name]);
  }
  const patchPath = host.joinPath(dir, "cordis.patch.yml");
  if (await host.stat(patchPath) == null && typeof manifest.patch === "string") {
    await host.writeTextFile(patchPath, manifest.patch);
  }
}
async function materializeHome(host, homeRoot, entries) {
  for (const [entryPath, data] of Object.entries(entries)) {
    if (!entryPath.startsWith("home/")) continue;
    const rel = safeRel(entryPath.slice("home/".length));
    if (!rel) continue;
    await host.writeFile(host.joinPath(homeRoot, rel), data);
  }
}
async function materializeProfile(host, profileDir, name, unit, entries) {
  await host.mkdir(profileDir);
  const prefix = `overrides/profiles/${name}/`;
  for (const [entryPath, data] of Object.entries(entries)) {
    if (!entryPath.startsWith(prefix)) continue;
    const rel = safeRel(entryPath.slice(prefix.length));
    if (!rel) continue;
    await host.writeFile(host.joinPath(profileDir, rel), data);
  }
  const pkg = {
    name: `dsh-profile-${name}`,
    private: true,
    dependencies: coordsToPkgDeps(unit.dependencies ?? {}),
    dsh: { profile: { bundles: unit.bundles ?? [] } }
  };
  await host.writeTextFile(host.joinPath(profileDir, "package.json"), JSON.stringify(pkg, null, 2) + "\n");
  const patchPath = host.joinPath(profileDir, "cordis.patch.yml");
  if (await host.stat(patchPath) == null && typeof unit.patch === "string") {
    await host.writeTextFile(patchPath, unit.patch);
  }
}
async function materializeHomeOverrides(host, homeRoot, entries) {
  for (const [entryPath, data] of Object.entries(entries)) {
    if (!entryPath.startsWith("overrides/")) continue;
    const rel = safeRel(entryPath.slice("overrides/".length));
    if (!rel) continue;
    if (rel.startsWith("profiles/")) continue;
    await host.writeFile(host.joinPath(homeRoot, rel), data);
  }
}
async function pnpmInstall(host, target, opts, frozen) {
  const timeoutMs = opts.timeoutMs > 0 ? opts.timeoutMs : 10 * 60 * 1e3;
  const args = ["install"];
  if (frozen) args.push("--frozen-lockfile");
  if (opts.registry) args.push("--registry", opts.registry);
  let r = await host.exec("pnpm", args, { cwd: target, timeoutMs });
  if (frozen && r.status !== 0) {
    r = await host.exec("pnpm", ["install", ...opts.registry ? ["--registry", opts.registry] : []], { cwd: target, timeoutMs });
  }
  if (r.error) throw new Error(`pnpm install \u6267\u884C\u5931\u8D25\uFF1A${r.error}`);
  if (r.status !== 0) throw new Error(`pnpm install \u5931\u8D25\uFF08\u9000\u51FA\u7801 ${r.status ?? "\u672A\u77E5"}\uFF09`);
}
async function downloadFiles(host, target, files) {
  let count = 0;
  const tmp = await host.mkdtemp("dspack-files-");
  try {
    for (const f of files ?? []) {
      const rel = safeRel(f.path);
      const tmpFile = host.joinPath(tmp, `dl-${count}`);
      let ok = false;
      let lastErr = null;
      for (const url of f.urls) {
        try {
          await host.download(url, tmpFile);
          ok = true;
          break;
        } catch (e) {
          lastErr = e;
        }
      }
      if (!ok) throw new Error(`files[] \u4E0B\u8F7D\u5931\u8D25\uFF1A${rel}\uFF08${lastErr?.message ?? "\u65E0\u53EF\u7528\u6E90"}\uFF09`);
      const sha = await host.sha256File(tmpFile);
      const st = await host.stat(tmpFile);
      if (sha !== String(f.sha256).toLowerCase() || (st?.size ?? -1) !== f.size) {
        throw new Error(`files[] \u5B8C\u6574\u6027\u6821\u9A8C\u5931\u8D25\uFF1A${rel}`);
      }
      await host.move(tmpFile, host.joinPath(target, rel));
      count += 1;
    }
    return count;
  } finally {
    await host.rm(tmp, { recursive: true, force: true }).catch(() => {
    });
  }
}
async function reconcileProfile(host, profileDir, manifest) {
  const missing = [];
  const added = [];
  const pkgDeps = coordsToPkgDeps(manifest.dependencies ?? {});
  const bundles = [...manifest.bundles ?? []];
  for (const name of bundles) {
    if (!Object.hasOwn(pkgDeps, name)) continue;
    const pkg = await readJson2(host, host.joinPath(profileDir, "node_modules", name, "package.json"));
    if (!pkg || pkg.dsh?.bundle?.patch === void 0) missing.push(name);
  }
  for (const name of Object.keys(pkgDeps)) {
    const pkg = await readJson2(host, host.joinPath(profileDir, "node_modules", name, "package.json"));
    if (pkg && pkg.dsh?.bundle?.patch !== void 0 && !bundles.includes(name)) {
      bundles.push(name);
      added.push(name);
    }
  }
  return { missing, added };
}
function parseJson3(raw) {
  if (raw == null || raw === "") return null;
  try {
    return raw instanceof Uint8Array ? JSON.parse(decodeText(raw)) : JSON.parse(raw);
  } catch {
    return null;
  }
}
async function readJson2(host, p) {
  return parseJson3(await host.readTextFile(p));
}
function safeRel(rel) {
  const r = String(rel).replace(/\\/g, "/");
  if (!r || r.startsWith("/") || /^[a-zA-Z]:/.test(r)) throw new Error(`\u975E\u6CD5\u76F8\u5BF9\u8DEF\u5F84\uFF1A${rel}`);
  if (r.split("/").includes("..")) throw new Error(`\u8DEF\u5F84\u542B\u5371\u9669\u6BB5 '..'\uFF1A${rel}`);
  return r;
}

// bridge.mjs
var emit = (type, value) => process.stdout.write(JSON.stringify({ type, ...value }) + "\n");
var LauncherHost = class extends NodeHost {
  constructor(pnpmCli) {
    super();
    this.pnpmCli = pnpmCli;
  }
  async exec(command, args, options = {}) {
    if (command !== "pnpm" || !this.pnpmCli) throw new Error("\u672A\u51C6\u5907\u597D pnpm \u8FD0\u884C\u73AF\u5883\u3002");
    return new Promise((resolve) => {
      const child = spawn2(process.execPath, [this.pnpmCli, ...args], {
        cwd: options.cwd,
        shell: false,
        windowsHide: true,
        stdio: ["ignore", "pipe", "pipe"]
      });
      for (const stream of [child.stdout, child.stderr]) {
        const lines = createInterface({ input: stream });
        lines.on("line", (detail) => emit("log", { detail: detail.slice(0, 4e3) }));
      }
      child.on("error", (error) => resolve({ status: null, error: error.message }));
      child.on("close", (status) => resolve({ status }));
    });
  }
};
function validateRelative(name) {
  const normalized = name.replaceAll("\\", "/").replace(/\/$/, "");
  if (!normalized || normalized.startsWith("/") || /[:\x00]/.test(normalized) || normalized.split("/").some((part) => !part || part === ".." || part === "."))
    throw new Error(`\u5305\u5185\u8DEF\u5F84\u4E0D\u5B89\u5168\uFF1A${name}`);
}
async function readPack(source) {
  const host = new NodeHost();
  const result = await inspectPack(host, source);
  if (!result.valid) throw new Error(result.validation.join("\uFF1B"));
  if (!(result.containerVersion === 3 && result.manifest.manifestVersion === 5 || result.containerVersion === 2 && result.manifest.manifestVersion === 4))
    throw new Error("\u5F53\u524D\u5185\u7F6E\u5F15\u64CE\u652F\u6301 .dspack v2/v3 \u4E0E manifest v4/v5\u3002");
  for (const entry of [...result.machine, ...result.overrides, ...result.home, ...result.other])
    validateRelative(entry.path);
  const manifest = result.manifest;
  const units = manifest.type === "dshhome" ? Object.values(manifest.profiles ?? {}) : [manifest];
  if (manifest.vendored || units.some((unit) => unit.vendored) || result.other.some((entry) => entry.path.startsWith("vendor/")))
    throw new Error("\u6B64\u5305\u4F7F\u7528 vendored \u79BB\u7EBF\u4F9D\u8D56\u6269\u5C55\uFF0C\u5F53\u524D\u5185\u7F6E\u5F15\u64CE\u5C1A\u4E0D\u652F\u6301\u3002");
  const profiles = manifest.type === "dshhome" ? Object.keys(manifest.profiles) : [sanitizeSlug(manifest.profileName || manifest.name)];
  for (const profile of profiles) {
    if (sanitizeSlug(profile) !== profile || !profile) throw new Error(`Profile \u540D\u79F0\u4E0D\u53EF\u7528\uFF1A${profile}`);
  }
  for (const file of [...manifest.files ?? [], ...(manifest.skills ?? []).filter((skill) => skill.sha256)])
    validateRelative(file.path);
  const localized = (value) => typeof value === "string" ? value : value?.zh || value?.en || manifest.name;
  return {
    name: manifest.name,
    title: localized(manifest.displayName),
    version: manifest.version,
    type: manifest.type || "profile",
    dshVersion: manifest.dshVersion || "",
    profiles,
    defaultProfile: manifest.type === "dshhome" ? manifest.defaultProfile : profiles[0],
    bundleCount: units.reduce((count, unit) => count + (unit.bundles?.length || 0), 0),
    dependencyCount: units.reduce((count, unit) => count + Object.keys(unit.dependencies ?? {}).length, 0),
    fileCount: result.totalEntries,
    sha256: result.sha256,
    size: result.size
  };
}
async function run(request) {
  switch (request.command) {
    case "inspect":
      return readPack(request.source);
    case "install": {
      const info = await readPack(request.source);
      const home = path2.resolve(request.home);
      try {
        await fs2.stat(home);
        throw new Error("\u5B89\u88C5\u76EE\u6807\u5DF2\u5B58\u5728\uFF0C\u8BF7\u521B\u5EFA\u65B0\u7684\u72EC\u7ACB\u5B9E\u4F8B\u3002");
      } catch (error) {
        if (error.code !== "ENOENT") throw error;
      }
      const result = await installPack(new LauncherHost(request.pnpmCli), {
        source: request.source,
        home,
        profilesRoot: path2.join(home, "profiles"),
        installedDshVersions: [request.dshVersion],
        force: false,
        onProgress: (stage, detail) => emit("progress", { stage, detail })
      });
      return { ...info, installed: result.installed, home };
    }
    case "inspectProfile": {
      const result = await inspectProfile(new NodeHost(), { name: path2.basename(request.source), dir: request.source });
      return { manifest: result.manifest, files: result.files.length, excluded: result.excluded.length };
    }
    case "export":
      return packProfile(new NodeHost(), { name: path2.basename(request.source), dir: request.source }, {
        out: request.output,
        dshVersion: request.dshVersion,
        force: false
      });
    default:
      throw new Error("\u672A\u77E5\u7684\u6574\u5408\u5305\u64CD\u4F5C\u3002");
  }
}
if (process.argv.includes("--stdio")) {
  try {
    let input = "";
    for await (const chunk of process.stdin) {
      input += chunk;
      if (input.length > 256e3) throw new Error("\u8BF7\u6C42\u8FC7\u5927\u3002");
    }
    const result = await run(JSON.parse(input));
    emit("result", { result });
  } catch (error) {
    emit("error", { detail: error.message });
    process.exitCode = 1;
  }
}
export {
  run
};
