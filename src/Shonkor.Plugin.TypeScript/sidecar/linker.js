'use strict';

/*
 * Shonkor JS/TS SEMANTIC LINKER (#294, epic #296).
 *
 * The whole-program counterpart to the per-file parser in index.js. Where #293 emits symbol nodes and
 * purely-syntactic SAME-FILE heritage, this pass builds ONE `ts.createProgram` + `getTypeChecker()` over the
 * whole compilation and resolves CROSS-FILE semantic edges on NODE IDS (never names/paths):
 *   - CALLS              — invocation -> callee method/function
 *   - REFERENCES_TYPE    — a type usage -> the referenced type
 *   - OVERRIDES          — a class member -> the base-class member it overrides
 *   - IMPLEMENTS_MEMBER  — a class member -> the interface member it satisfies
 *   - EXTENDS/IMPLEMENTS  — heritage sharpened cross-file to the REAL target node (the type-checker resolves
 *                          the base even when it lives in another module; the store's MIN-provenance upsert
 *                          then upgrades #293's INFERRED same-file variant to EXTRACTED).
 *
 * This is the TS pendant to the C# SemanticCsharpLinker.cs (edge set) + RoslynSemantics.ToNodeId (id mapping).
 * INSTANTIATES is deliberately NOT emitted (out of #294's acceptance criteria).
 *
 * Node-ID parity is a CONTRACT: the ids these edges point at are rebuilt with the SAME shared helpers the
 * parser uses (./nodeIds.js), from the resolved symbol's declaration — so an edge lands on exactly the node
 * #293 emitted. A symbol declared only externally (node_modules / *.d.ts) has no node and its edge is SKIPPED
 * (no dangling — AC#4), mirroring RoslynSemantics returning null for metadata-only symbols.
 *
 * Provenance: this module only reports the edge triples; the HOST stamps them Provenance.Extracted and only
 * for typed TS (.ts/.tsx). Untyped JS (.js/.jsx) source files are skipped here — those stay #293/#295's
 * best-effort/INFERRED tier (no EXTRACTED claim without type information).
 *
 * Known limitation (inherited, ratified on #294): CALLS/OVERRIDES/IMPLEMENTS_MEMBER land at METHOD granularity
 * (by name), not per-overload — an overload set is ONE TS symbol (see nodeIds.js). Overload precision is the
 * follow-up #321. Accessors, by contrast, ARE precise: a get/set pair shares one symbol, so the target `:get`
 * vs `:set` is chosen from the reference's read/write context (references) or the source member's own kind
 * (member-level OVERRIDES/IMPLEMENTS_MEMBER).
 */

const fs = require('fs');
const path = require('path');

const { componentIdFor, symbolIdFor, memberIdFor } = require('./nodeIds');

// Only typed TS is eligible to ORIGINATE EXTRACTED edges. Untyped JS is left to #293/#295.
const TYPED_EXTS = new Set(['.ts', '.tsx']);

/*
 * Path parity is a CONTRACT with the #293 parser. The parser builds node ids from the EXACT filePath string
 * the host sent (e.g. a Windows `C:\proj\a.ts` with backslashes). TypeScript, however, normalises
 * `sourceFile.fileName` to forward slashes. So we map each program file's normalised name BACK to the
 * original rootName string before deriving an id — otherwise every edge would point at a forward-slash
 * variant that matches no parser node. Set per-request in link() (the sidecar handles one request at a time).
 */
let originalPathOf = (fileName) => fileName;

/*
 * Under-linking observability (#323). The ONLY degeneration mode of this linker is SILENT under-linking: when
 * `originalPathOf` cannot map a program source file's forward-slash `fileName` back to an original `rootName`
 * (real with symlink/realpath — TS runs default `preserveSymlinks:false` and resolves declarations to a
 * realpath OUTSIDE the rootNames; pnpm/monorepo trees), the derived id is the unmapped forward-slash variant,
 * which matches NO #293 parser node -> the host's knownIds backstop drops ALL of that file's edges quietly.
 *
 * We surface it (WITHOUT resolving the symlink itself — that is a separate ticket). `isRootFile` answers "is
 * this TS fileName one of the input rootNames?"; when a real PROJECT source file (typed TS, not a `.d.ts`, not
 * node_modules) is NOT a rootName, its id will not round-trip, so we record it in `parityMisses` and link()
 * emits a per-file warning. External symbols (`.d.ts`/node_modules) legitimately have no node and are still
 * skipped silently (AC#4 of #294) — only the rootName-miss (a project file that SHOULD have mapped) warns.
 */
let isRootFile = () => false;
const parityMisses = new Set();

/** True for a path inside a node_modules tree — an external dependency, never a first-party project file. */
function isExternalPath(fileName) {
  return /[\\/]node_modules[\\/]/.test(fileName);
}

/** The module / JSComponent node id for a source file (parity with index.js's parseRequest). */
function moduleIdOf(sourceFile) {
  const fileName = sourceFile.fileName;
  // Record a parity miss (#323): a typed-TS project source file (not a `.d.ts`, not node_modules) whose TS
  // fileName is not one of the rootNames — its id will fall through to the unmapped variant and be dropped by
  // the host backstop. `.js`/`.jsx` targets are owned by #293/#295 (never linked here), so they never warn.
  if (
    TYPED_EXTS.has(path.extname(fileName).toLowerCase()) &&
    !sourceFile.isDeclarationFile &&
    !isExternalPath(fileName) &&
    !isRootFile(fileName)
  ) {
    parityMisses.add(fileName);
  }
  const fp = originalPathOf(fileName);
  const componentName = path.basename(fp).replace(/\.[^.]+$/, '');
  return componentIdFor(fp, componentName);
}

/** The declared name of a declaration, verbatim; null for anonymous/unsupported names (parity with parser). */
function declaredName(ts, node) {
  const name = node.name;
  if (!name) return null;
  if (ts.isIdentifier(name)) return name.text;
  if (ts.isStringLiteralLike && ts.isStringLiteralLike(name)) return name.text;
  try {
    return name.getText();
  } catch {
    return null;
  }
}

/** True when a declaration sits directly at module scope (the only declarations the #293 parser turns into nodes). */
function isTopLevel(ts, node) {
  return !!node.parent && ts.isSourceFile(node.parent);
}

/** True for the top-level declaration kinds the parser emits as symbol nodes. */
function isTopLevelSymbolDecl(ts, node) {
  return (
    ts.isClassDeclaration(node) ||
    ts.isInterfaceDeclaration(node) ||
    ts.isFunctionDeclaration(node) ||
    ts.isEnumDeclaration(node) ||
    ts.isTypeAliasDeclaration(node)
  );
}

/** The (memberName, accessorKind) for a class/interface member declaration, mirroring collectMembers in index.js. */
function memberNameAndKind(ts, decl) {
  if (ts.isGetAccessorDeclaration(decl)) return { name: declaredName(ts, decl), kind: 'get', ok: true };
  if (ts.isSetAccessorDeclaration(decl)) return { name: declaredName(ts, decl), kind: 'set', ok: true };
  if (ts.isMethodDeclaration(decl) || ts.isMethodSignature(decl)) return { name: declaredName(ts, decl), kind: null, ok: true };
  if (ts.isConstructorDeclaration(decl)) return { name: 'constructor', kind: null, ok: true };
  if (ts.isPropertyDeclaration(decl) || ts.isPropertySignature(decl)) return { name: declaredName(ts, decl), kind: null, ok: true };
  return { ok: false, name: null, kind: null };
}

/**
 * The Shonkor node id for a specific DECLARATION node, or null when the parser would not have emitted a node
 * for it (nested/local declarations, external/metadata-only *.d.ts symbols). This is the TS pendant to
 * RoslynSemantics.ToNodeId, but it works off the declaration's syntactic position (matching how the #293
 * parser walks `sourceFile.statements` and class/interface members) so the ids are byte-identical.
 */
function declarationNodeId(ts, decl) {
  if (!decl) return null;
  const sf = decl.getSourceFile && decl.getSourceFile();
  // External / metadata-only symbol (node_modules or an ambient *.d.ts) — no node exists, so skip (AC#4).
  if (!sf || sf.isDeclarationFile) return null;

  const moduleId = moduleIdOf(sf);

  if (isTopLevel(ts, decl)) {
    if (!isTopLevelSymbolDecl(ts, decl)) return null;
    const name = declaredName(ts, decl);
    return name ? symbolIdFor(moduleId, name) : null;
  }

  // A member is a node only when its OWNER is a top-level class/interface (the only containers the parser
  // descends into). Nested classes, members of a locally-declared class, etc. have no node.
  const owner = decl.parent;
  if (
    owner &&
    (ts.isClassDeclaration(owner) || ts.isInterfaceDeclaration(owner)) &&
    isTopLevel(ts, owner)
  ) {
    const ownerName = declaredName(ts, owner);
    if (!ownerName) return null;
    const m = memberNameAndKind(ts, decl);
    if (!m.ok || !m.name) return null;
    return memberIdFor(moduleId, ownerName, m.name, m.kind);
  }

  return null;
}

/** Follow an alias symbol (an imported binding) to the symbol it actually refers to. */
function unalias(ts, checker, symbol) {
  if (symbol && symbol.flags & ts.SymbolFlags.Alias) {
    try {
      return checker.getAliasedSymbol(symbol);
    } catch {
      // keep the original on failure
    }
  }
  return symbol;
}

/**
 * Pick the declaration of a symbol that best matches an optional accessor kind. For a get/set pair (which
 * share ONE symbol with declarations=[get,set]) the `preferKind` selects the matching accessor; otherwise a
 * body-bearing declaration (the overload implementation) is preferred for id stability, else the first.
 */
function pickDeclaration(ts, symbol, preferKind) {
  const decls = (symbol && symbol.declarations) || [];
  if (decls.length === 0) return null;
  if (preferKind) {
    const match = decls.find((d) =>
      preferKind === 'get' ? ts.isGetAccessorDeclaration(d) : ts.isSetAccessorDeclaration(d));
    if (match) return match;
  }
  const impl = decls.find((d) => d.body);
  return impl || decls[0];
}

/** The node id a resolved symbol maps to (unaliased), honouring an accessor kind. Null when it has no node. */
function nodeIdForSymbol(ts, checker, symbol, preferKind) {
  symbol = unalias(ts, checker, symbol);
  if (!symbol) return null;
  return declarationNodeId(ts, pickDeclaration(ts, symbol, preferKind));
}

/**
 * The candidate declarations of a symbol for the DISTINCT-node-id set (#295 AC#3). Unlike pickDeclaration,
 * this keeps ALL declarations (optionally narrowed to a matching accessor kind) so the caller can detect real
 * ambiguity: a union-typed property access `x: A|B; x.m()` yields ONE synthetic symbol whose declarations are
 * `A.m` and `B.m` (empirically verified — spike on #295). An overload set is also multiple declarations, but
 * they share owner+name → ONE node id → the caller's set collapses to size 1 (not ambiguous). A get/set pair
 * is likewise collapsed: the accessor filter keeps only the one matching the usage kind.
 */
function candidateDeclarations(ts, symbol, preferKind) {
  const decls = (symbol && symbol.declarations) || [];
  if (decls.length === 0) return [];
  if (preferKind) {
    const matched = decls.filter((d) =>
      preferKind === 'get' ? ts.isGetAccessorDeclaration(d) : ts.isSetAccessorDeclaration(d));
    if (matched.length > 0) return matched;
  }
  return decls;
}

/**
 * The DISTINCT set of node ids a resolved (already-unaliased) symbol maps to, honouring an accessor kind.
 * `size == 0` -> no node (external / metadata-only, skip). `size == 1` -> a single unambiguous target.
 * `size > 1` -> genuine multi-candidate ambiguity (only a union-typed property access reaches here in practice).
 */
function idSetFromResolved(ts, resolved, preferKind) {
  if (!resolved) return [];
  const ids = new Set();
  for (const decl of candidateDeclarations(ts, resolved, preferKind)) {
    const id = declarationNodeId(ts, decl);
    if (id) ids.add(id);
  }
  return [...ids];
}

/** The distinct node-id set for a plain symbol reference (REFERENCES_TYPE — no accessor context). */
function symbolIdSet(ts, checker, symbol) {
  return idSetFromResolved(ts, unalias(ts, checker, symbol), null);
}

/** Whether a reference node sits in a write position (the LHS of a plain `=`), so an accessor means the setter. */
function isWritePosition(ts, node) {
  const p = node.parent;
  return !!p && ts.isBinaryExpression(p) && p.left === node && p.operatorToken.kind === ts.SyntaxKind.EqualsToken;
}

/**
 * The distinct node-id set for a symbol referenced AT a usage site (CALLS). When the symbol is an accessor,
 * the read/write context of the usage decides `:get` vs `:set` (ratified Ergänzung 1) — a bare `owner::name`
 * node never exists. A union-typed callee (`x: A|B; x.m()`) yields >1 id -> the caller marks each edge ambiguous.
 */
function referenceIdSet(ts, checker, usageNode, symbol) {
  const resolved = unalias(ts, checker, symbol);
  if (!resolved) return [];
  let preferKind = null;
  if (resolved.flags & (ts.SymbolFlags.GetAccessor | ts.SymbolFlags.SetAccessor)) {
    preferKind = isWritePosition(ts, usageNode) ? 'set' : 'get';
  }
  return idSetFromResolved(ts, resolved, preferKind);
}

/** The id of the nearest enclosing declaration matching `predicate` that maps to a node, else null. */
function enclosingNodeId(ts, node, predicate) {
  for (let cur = node.parent; cur; cur = cur.parent) {
    if (ts.isSourceFile(cur)) break;
    if (predicate(cur)) {
      const id = declarationNodeId(ts, cur);
      if (id) return id;
    }
  }
  return null;
}

const isCallable = (ts) => (n) =>
  ts.isMethodDeclaration(n) ||
  ts.isGetAccessorDeclaration(n) ||
  ts.isSetAccessorDeclaration(n) ||
  ts.isConstructorDeclaration(n) ||
  ts.isFunctionDeclaration(n);

const isTypeLevel = (ts) => (n) =>
  ts.isClassDeclaration(n) ||
  ts.isInterfaceDeclaration(n) ||
  ts.isFunctionDeclaration(n) ||
  ts.isEnumDeclaration(n) ||
  ts.isTypeAliasDeclaration(n);

/** Emit cross-file heritage (EXTENDS/IMPLEMENTS) sharpened to the checker-resolved base node. */
function emitHeritage(ts, checker, decl, addEdge) {
  const sourceId = declarationNodeId(ts, decl);
  if (!sourceId) return;
  for (const clause of decl.heritageClauses || []) {
    const rel = clause.token === ts.SyntaxKind.ExtendsKeyword ? 'EXTENDS' : 'IMPLEMENTS';
    for (const t of clause.types) {
      const sym = checker.getSymbolAtLocation(t.expression);
      const targetId = nodeIdForSymbol(ts, checker, sym, null);
      addEdge(sourceId, targetId, rel);
    }
  }
}

/**
 * Emit member-level OVERRIDES (against base CLASS members) / IMPLEMENTS_MEMBER (against INTERFACE members) for
 * one class declaration. The two are told apart by the heritage clause the base came from (extends -> class,
 * implements -> interface), which the checker resolves cross-file. A base type's `getProperty` resolves
 * INHERITED members too, so a member declared in a grandparent/base-interface still lands on its real node.
 */
function emitMemberOverrides(ts, checker, classDecl, addEdge) {
  const extendsTypes = [];
  const implementsTypes = [];
  for (const clause of classDecl.heritageClauses || []) {
    const bucket = clause.token === ts.SyntaxKind.ExtendsKeyword ? extendsTypes : implementsTypes;
    for (const t of clause.types) {
      try {
        const type = checker.getTypeFromTypeNode(t);
        if (type) bucket.push(type);
      } catch {
        // Unresolvable base — skip (no dangling).
      }
    }
  }
  if (extendsTypes.length === 0 && implementsTypes.length === 0) return;

  const link = (member, sourceId, bases, relationship) => {
    const info = memberNameAndKind(ts, member);
    if (!info.ok || !info.name) return;
    for (const base of bases) {
      const baseSym = base.getProperty ? base.getProperty(info.name) : undefined;
      if (!baseSym) continue;
      // Match the source member's accessor kind against the base declaration (get overrides get, set set).
      const targetId = declarationNodeId(ts, pickDeclaration(ts, baseSym, info.kind));
      if (targetId) addEdge(sourceId, targetId, relationship);
    }
  };

  for (const member of classDecl.members || []) {
    const sourceId = declarationNodeId(ts, member);
    if (!sourceId) continue;
    link(member, sourceId, extendsTypes, 'OVERRIDES');
    link(member, sourceId, implementsTypes, 'IMPLEMENTS_MEMBER');
  }
}

/** Walk one source file, emitting all in-scope semantic edges. */
function walkFile(ts, checker, sourceFile, addEdge) {
  const callablePred = isCallable(ts);
  const typeLevelPred = isTypeLevel(ts);

  const visit = (node) => {
    // Heritage sharpening + member override/implement resolution on each class/interface.
    if (ts.isClassDeclaration(node)) {
      emitHeritage(ts, checker, node, addEdge);
      emitMemberOverrides(ts, checker, node, addEdge);
    } else if (ts.isInterfaceDeclaration(node)) {
      emitHeritage(ts, checker, node, addEdge);
    }

    // CALLS — invocation -> callee (method/function). `new Foo()` is INSTANTIATES (out of scope) and is a
    // NewExpression, so it is naturally excluded here. A union-typed callee resolves to >1 candidate node id
    // (#295 AC#3): one CALLS edge per candidate, each flagged ambiguous so the host stamps AMBIGUOUS.
    if (ts.isCallExpression(node)) {
      const sym = checker.getSymbolAtLocation(node.expression);
      const targetIds = referenceIdSet(ts, checker, node.expression, sym);
      if (targetIds.length > 0) {
        const sourceId = enclosingNodeId(ts, node, callablePred);
        const ambiguous = targetIds.length > 1;
        for (const targetId of targetIds) addEdge(sourceId, targetId, 'CALLS', ambiguous);
      }
    }

    // REFERENCES_TYPE — every type usage, attributed to the enclosing type-level declaration. A union type
    // alias reference resolving to >1 target is ambiguous the same way (#295 AC#3).
    if (ts.isTypeReferenceNode(node)) {
      const sym = checker.getSymbolAtLocation(node.typeName);
      const targetIds = symbolIdSet(ts, checker, sym);
      if (targetIds.length > 0) {
        const sourceId = enclosingNodeId(ts, node, typeLevelPred);
        const ambiguous = targetIds.length > 1;
        for (const targetId of targetIds) addEdge(sourceId, targetId, 'REFERENCES_TYPE', ambiguous);
      }
    }

    ts.forEachChild(node, visit);
  };

  ts.forEachChild(sourceFile, visit);
}

/**
 * Resolve the compiler options for the program: the nearest tsconfig (walking up from projectDir) when present,
 * else permissive defaults. Emit is always off (analysis only).
 */
function loadCompilerOptions(ts, projectDir, diagnostics) {
  const defaults = {
    allowJs: true,
    checkJs: false,
    noEmit: true,
    allowNonTsExtensions: true,
    target: ts.ScriptTarget ? ts.ScriptTarget.Latest : 99,
    module: ts.ModuleKind ? ts.ModuleKind.ESNext : undefined,
    jsx: ts.JsxEmit ? ts.JsxEmit.Preserve : undefined,
    moduleResolution:
      ts.ModuleResolutionKind && ts.ModuleResolutionKind.Bundler
        ? ts.ModuleResolutionKind.Bundler
        : (ts.ModuleResolutionKind ? ts.ModuleResolutionKind.NodeJs : undefined),
  };
  if (!projectDir) return defaults;
  try {
    const configPath = ts.findConfigFile(projectDir, (p) => fs.existsSync(p), 'tsconfig.json');
    if (!configPath) return defaults;
    const read = ts.readConfigFile(configPath, (p) => fs.readFileSync(p, 'utf8'));
    if (read.error) return defaults;
    const parsed = ts.parseJsonConfigFileContent(read.config, ts.sys, path.dirname(configPath));
    return { ...parsed.options, noEmit: true };
  } catch (e) {
    diagnostics.push({ severity: 'warning', message: `link: could not load tsconfig from ${projectDir}: ${e && e.message ? e.message : String(e)}` });
    return defaults;
  }
}

/**
 * Handle a `link` request: `{ id, kind: "link", rootNames: [...], projectDir, emitFrom? }`. Builds ONE program
 * over `rootNames`, then emits edges ORIGINATING from the typed-TS source files (optionally narrowed to the
 * `emitFrom` subset — the file-subset seam kept open for the incremental relink follow-up #318). Returns the
 * edge triples plus diagnostics; the host filters danglers and stamps provenance.
 */
function link(ts, req) {
  const diagnostics = [];
  const rootNames = Array.isArray(req.rootNames) ? req.rootNames.filter((r) => typeof r === 'string' && r.length > 0) : [];
  if (rootNames.length === 0) {
    return { edges: [], diagnostics, meta: { rootCount: 0 } };
  }
  const projectDir = req.projectDir || path.dirname(rootNames[0]);
  const emitFrom =
    Array.isArray(req.emitFrom) && req.emitFrom.length > 0
      ? new Set(req.emitFrom.map((f) => f.replace(/\\/g, '/')))
      : null;

  // Map TS's forward-slash fileName back to the original (possibly backslash) rootName for id parity.
  const rootByNormalized = new Map();
  for (const r of rootNames) rootByNormalized.set(r.replace(/\\/g, '/'), r);
  originalPathOf = (fileName) =>
    rootByNormalized.get(fileName) || rootByNormalized.get(fileName.replace(/\\/g, '/')) || fileName;
  // Parity-miss detection (#323): a fileName that round-trips through the map is a rootName; anything else is
  // an unmapped project file (or an external/.d.ts, which moduleIdOf filters out before recording a miss).
  isRootFile = (fileName) =>
    rootByNormalized.has(fileName) || rootByNormalized.has(fileName.replace(/\\/g, '/'));
  parityMisses.clear();

  const options = loadCompilerOptions(ts, projectDir, diagnostics);
  const program = ts.createProgram(rootNames, options);
  const checker = program.getTypeChecker();

  // Deduplicate by triple. `ambiguous` is additive/optional in the wire contract: emitted ONLY when true, so
  // an absent field means a normal (host-stamped EXTRACTED) edge — backward compatible. If the same triple is
  // reached both ambiguously and unambiguously, the unambiguous resolution wins (clear the flag): the edge IS
  // provable from at least one site, so it must not be downgraded to AMBIGUOUS.
  const byKey = new Map();
  const addEdge = (sourceId, targetId, relationship, ambiguous = false) => {
    if (!sourceId || !targetId || sourceId === targetId) return;
    const key = `${sourceId} ${targetId} ${relationship}`;
    const existing = byKey.get(key);
    if (existing) {
      if (!ambiguous) existing.ambiguous = false;
      return;
    }
    byKey.set(key, { sourceId, targetId, relationship, ambiguous });
  };

  let filesWalked = 0;
  for (const sourceFile of program.getSourceFiles()) {
    if (sourceFile.isDeclarationFile) continue;
    const fileName = sourceFile.fileName;
    if (!TYPED_EXTS.has(path.extname(fileName).toLowerCase())) continue; // untyped JS -> #293/#295
    if (emitFrom && !emitFrom.has(fileName)) continue; // subset seam (#318)
    walkFile(ts, checker, sourceFile, addEdge);
    filesWalked++;
  }

  // Serialise: drop the `ambiguous` field when false so the payload stays byte-identical to the pre-#295 wire
  // format for the common (unambiguous) case.
  const edges = [...byKey.values()].map((e) =>
    e.ambiguous
      ? { sourceId: e.sourceId, targetId: e.targetId, relationship: e.relationship, ambiguous: true }
      : { sourceId: e.sourceId, targetId: e.targetId, relationship: e.relationship });

  // #323 (AC#1): surface each parity miss as a WARNING WITH THE FILE NAME instead of a silent drop. Every edge
  // touching this file is under-linked (dropped by the host's knownIds backstop), so the cross-file semantic
  // links for it are missing — the exact risk the generic "N dangling skipped" info-log hid.
  const parityMissFiles = [...parityMisses];
  for (const fileName of parityMissFiles) {
    diagnostics.push({
      severity: 'warning',
      message:
        `link: id-parity miss for project file '${fileName}' — its TS path did not map back to an input ` +
        `rootName, so its cross-file edges are dropped (silent under-linking). Likely a symlink/realpath ` +
        `(TS default preserveSymlinks:false resolves declarations to a realpath outside the rootNames).`,
    });
  }

  // #323 (AC#2): a coarse parity summary (miss count) rides along in meta so a regression is observable at a
  // glance without parsing the per-file warnings.
  return {
    edges,
    diagnostics,
    meta: {
      rootCount: rootNames.length,
      filesWalked,
      edgeCount: edges.length,
      parityMissCount: parityMissFiles.length,
      parityMissFiles,
    },
  };
}

module.exports = { link };
