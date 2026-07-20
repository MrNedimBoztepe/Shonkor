'use strict';

/*
 * Shonkor JS/TS base parser sidecar (#292, epic #296).
 *
 * Transport: line-delimited JSON over stdio. Each request is one line:
 *   { "id": <number>, "filePath": <string>, "content": <string> }
 * Each request yields exactly one response line (id-correlated):
 *   { "id": <number>, "nodes": [...], "edges": [...], "diagnostics": [...], "meta": {...} }
 *
 * Engine: the RAW TypeScript Compiler API (not ts-morph). We prefer the analysed project's own
 * `typescript` when it exposes a stable program API (major 5.x or 6.x); otherwise we fall back to the
 * bundled, pinned 6.x. Rationale (ratified design note on #292): TS 7.0 is the native port whose stable
 * API only lands in 7.1, and ts-morph breaks against 7.0 — so 6.x is the safe floor for this foundation
 * ticket. #294 revisits richer resolution.
 *
 * Scope of #292: transport + adapter contract + tsconfig LOADING (basis for #294) + diagnostics that are
 * never swallowed. No symbol nodes (#293), no semantic edges (#294).
 *
 * Parent-death safety net (belt-and-suspenders with the host-side Job Object): when the parent process
 * exits, our stdin closes; we exit on that so we never linger as an orphan on any platform.
 */

const fs = require('fs');
const path = require('path');
const readline = require('readline');

const PROBE_EXTENSIONS = ['.ts', '.tsx', '.js', '.jsx'];

// Cache the resolved typescript module per resolution key so we don't re-require per file.
/** @type {Map<string, {ts: any, source: string, version: string}>} */
const tsCache = new Map();
// Cache tsconfig lookup per containing directory; value: { found, path, error, announced }.
/** @type {Map<string, {found: boolean, path: string|null, error: string|null, announced: boolean}>} */
const tsconfigCache = new Map();

let bundledTs = null;
let bundledTsPath = null;
function getBundledTs() {
  if (!bundledTs) {
    // Resolves to this sidecar's own node_modules/typescript (the pinned 6.x).
    bundledTsPath = require.resolve('typescript');
    bundledTs = require(bundledTsPath);
  }
  return bundledTs;
}

/**
 * Resolve the typescript module to use for a given project directory: project-local first when it exposes
 * a stable program API (major 5 or 6), else the bundled pinned 6.x.
 */
function resolveTypeScript(projectDir) {
  const key = projectDir || '<none>';
  const cached = tsCache.get(key);
  if (cached) return cached;

  // Make sure the bundled path is known so we can tell a genuine project-local copy from our own.
  getBundledTs();

  let resolved = null;
  if (projectDir) {
    try {
      const localPath = require.resolve('typescript', { paths: [projectDir] });
      // require.resolve can fall back to our own bundled copy when the project has none — that is NOT a
      // project-local typescript, so only treat a DIFFERENT path as project-local.
      if (localPath !== bundledTsPath) {
        const localTs = require(localPath);
        const major = parseInt(String(localTs.version).split('.')[0], 10);
        // A stable Program API exists on 5.x and 6.x. 7.0 is the native port whose stable API lands in 7.1.
        if ((major === 5 || major === 6) && typeof localTs.createSourceFile === 'function') {
          resolved = { ts: localTs, source: 'project', version: localTs.version };
        }
      }
    } catch {
      // No project-local typescript (or it failed to load) — fall through to the bundled copy.
    }
  }

  if (!resolved) {
    const ts = getBundledTs();
    resolved = { ts, source: 'bundled', version: ts.version };
  }

  tsCache.set(key, resolved);
  return resolved;
}

/**
 * Load (only load — basis for #294) the nearest tsconfig.json walking up from the project directory.
 * Cached per directory; the first successful load per directory is announced as an info diagnostic.
 */
function loadTsconfig(ts, projectDir, diagnostics) {
  if (!projectDir) return { found: false, path: null };
  let entry = tsconfigCache.get(projectDir);
  if (!entry) {
    entry = { found: false, path: null, error: null, announced: false };
    try {
      const configPath = ts.findConfigFile(projectDir, (p) => fs.existsSync(p), 'tsconfig.json');
      if (configPath) {
        const read = ts.readConfigFile(configPath, (p) => fs.readFileSync(p, 'utf8'));
        if (read.error) {
          entry.error = ts.flattenDiagnosticMessageText(read.error.messageText, '\n');
          entry.path = configPath;
        } else {
          // parseJsonConfigFileContent is the real "loaded and understood" step (basis for #294).
          const parsed = ts.parseJsonConfigFileContent(read.config, ts.sys, path.dirname(configPath));
          entry.found = true;
          entry.path = configPath;
          if (parsed.errors && parsed.errors.length > 0) {
            entry.error = parsed.errors
              .map((e) => ts.flattenDiagnosticMessageText(e.messageText, '\n'))
              .join('; ');
          }
        }
      }
    } catch (e) {
      entry.error = e && e.message ? e.message : String(e);
    }
    tsconfigCache.set(projectDir, entry);
  }

  if (entry.found && !entry.announced) {
    entry.announced = true;
    diagnostics.push({ severity: 'info', message: `Loaded tsconfig: ${entry.path}` });
  } else if (entry.error && !entry.announced) {
    entry.announced = true;
    diagnostics.push({ severity: 'warning', message: `tsconfig at ${entry.path} could not be fully loaded: ${entry.error}` });
  }
  return entry;
}

function scriptKindFor(ts, filePath) {
  const ext = path.extname(filePath).toLowerCase();
  switch (ext) {
    case '.tsx': return ts.ScriptKind.TSX;
    case '.jsx': return ts.ScriptKind.JSX;
    case '.js': return ts.ScriptKind.JS;
    default: return ts.ScriptKind.TS;
  }
}

/**
 * Resolve a relative import against the importing file's directory, probing the usual extensionless
 * conventions (mirrors the host's former JavaScriptParser.ResolveImportPath so node ids stay consistent).
 * Non-relative specifiers (package names) are returned as-is.
 */
function resolveImportPath(filePath, source) {
  if (!source.startsWith('.')) return source;
  const dir = path.dirname(filePath);
  const base = path.resolve(dir, source);
  try {
    if (fs.existsSync(base) && fs.statSync(base).isFile()) return base;
    for (const ext of PROBE_EXTENSIONS) {
      if (fs.existsSync(base + ext)) return base + ext;
    }
    for (const ext of PROBE_EXTENSIONS) {
      const idx = path.join(base, 'index' + ext);
      if (fs.existsSync(idx)) return idx;
    }
  } catch {
    // Probing is best-effort; fall through to the unresolved base path.
  }
  return base;
}

function collectImports(ts, sourceFile, filePath, componentNodeId, edges) {
  for (const statement of sourceFile.statements) {
    let specifier = null;
    if (ts.isImportDeclaration(statement) && statement.moduleSpecifier) {
      specifier = statement.moduleSpecifier;
    } else if (ts.isExportDeclaration(statement) && statement.moduleSpecifier) {
      // `export ... from '...'` is a module edge too.
      specifier = statement.moduleSpecifier;
    } else if (
      ts.isImportEqualsDeclaration &&
      ts.isImportEqualsDeclaration(statement) &&
      statement.moduleReference &&
      ts.isExternalModuleReference(statement.moduleReference) &&
      statement.moduleReference.expression
    ) {
      specifier = statement.moduleReference.expression;
    }

    if (!specifier || !ts.isStringLiteralLike(specifier)) continue;
    const source = specifier.text;
    if (!source || source.trim().length === 0) continue;

    edges.push({
      sourceId: componentNodeId,
      targetId: resolveImportPath(filePath, source),
      relationship: 'IMPORTS',
      properties: { rawSource: source },
    });
  }
}

function parseRequest(req) {
  const diagnostics = [];
  const filePath = req.filePath;
  const content = typeof req.content === 'string' ? req.content : '';
  const projectDir = filePath ? path.dirname(filePath) : null;

  const { ts, source: tsSource, version: tsVersion } = resolveTypeScript(projectDir);
  const tsconfig = loadTsconfig(ts, projectDir, diagnostics);

  const componentName = filePath ? path.basename(filePath).replace(/\.[^.]+$/, '') : 'module';
  const componentNodeId = `${filePath}::${componentName}`;

  const properties = {};
  // Parity with the former host parser: Sitecore JSS / Next.js signature hints on the JSComponent node.
  if (content.indexOf('@sitecore-jss/sitecore-jss-nextjs') !== -1) properties.isSitecoreJSS = 'true';
  if (content.indexOf('withDatasourceCheck') !== -1) properties.withDatasourceCheck = 'true';

  const nodes = [
    { id: componentNodeId, name: componentName, type: 'JSComponent', filePath, properties },
  ];
  const edges = [
    { sourceId: filePath, targetId: componentNodeId, relationship: 'CONTAINS' },
  ];

  const sourceFile = ts.createSourceFile(
    filePath || 'module.ts',
    content,
    ts.ScriptTarget ? ts.ScriptTarget.Latest : 99,
    /* setParentNodes */ true,
    scriptKindFor(ts, filePath || '.ts')
  );

  // Syntactic parse errors are SURFACED as diagnostics, never swallowed (AC#2 — the core departure from the
  // former Esprima tolerant parse which silently dropped advanced TS). Valid generics/decorators/enums
  // produce no diagnostics here and their nodes/edges are still emitted.
  const parseDiags = sourceFile.parseDiagnostics || [];
  for (const d of parseDiags) {
    let line;
    try {
      if (typeof d.start === 'number') {
        line = sourceFile.getLineAndCharacterOfPosition(d.start).line + 1;
      }
    } catch {
      line = undefined;
    }
    diagnostics.push({
      severity: 'error',
      code: d.code,
      line,
      message: ts.flattenDiagnosticMessageText(d.messageText, '\n'),
    });
  }

  collectImports(ts, sourceFile, filePath, componentNodeId, edges);

  return {
    nodes,
    edges,
    diagnostics,
    meta: {
      tsSource,
      tsVersion,
      tsconfig: { found: tsconfig.found, path: tsconfig.path || null },
    },
  };
}

function handleLine(line) {
  const trimmed = line.trim();
  if (trimmed.length === 0) return;

  let req;
  try {
    req = JSON.parse(trimmed);
  } catch {
    // Unparseable line — no id to answer to; log to stderr (never stdout, which carries the protocol).
    process.stderr.write('[ts-sidecar] dropped unparseable stdin line\n');
    return;
  }

  const id = req.id;

  // Deterministic test hook (gated by env): let a test force a hang so the host-side timeout is provable.
  if (process.env.SHONKOR_SIDECAR_TEST_HOOKS === '1' &&
      typeof req.content === 'string' && req.content.indexOf('__SHONKOR_TEST_HANG__') !== -1) {
    return; // Intentionally never answer this id — the host's per-request timeout must fire.
  }

  let payload;
  try {
    payload = parseRequest(req);
  } catch (e) {
    // A failure inside the sidecar must still produce a response (id → exactly one answer) with a diagnostic,
    // so the host never deadlocks waiting and the file is not silently dropped.
    payload = {
      nodes: [],
      edges: [],
      diagnostics: [{ severity: 'error', message: `sidecar parse failure: ${e && e.message ? e.message : String(e)}` }],
      meta: {},
    };
  }

  payload.id = id;
  process.stdout.write(JSON.stringify(payload) + '\n');
}

function main() {
  process.stdin.setEncoding('utf8');
  const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity, terminal: false });
  rl.on('line', handleLine);
  // Parent-death net: when the parent exits, stdin closes -> we exit rather than orphan.
  rl.on('close', () => process.exit(0));
}

main();
