'use strict';

/*
 * Shared node-ID scheme for the JS/TS sidecar (#317, single source of truth).
 *
 * The ID scheme is a CONTRACT: nodes, edges and downstream consumers all point at these ids, and the store
 * upserts nodes with `ON CONFLICT(Id)`. It used to live inline in index.js; it is extracted here so the
 * semantic linker (#294) can reference the SAME symbols as edge targets without re-deriving the strings.
 *
 * Layout (all segments joined by `::`, names VERBATIM / case-sensitive — never lowercased, so ids resolve on
 * case-preserving stores and Windows paths; the BUG-012 family):
 *   - module / JSComponent node : `${filePath}::${basename}`                    (2 `::`-segments)
 *   - top-level symbol          : `${moduleId}::${name}`                        (3 segments)
 *   - member                    : `${moduleId}::${ownerName}::${memberName}`    (4 segments)
 *
 * Because the module node has strictly fewer segments than any symbol, a class named exactly like its file
 * (Button.tsx -> `class Button`) can never share an id with — and thus never overwrite — the signal-bearing
 * JSComponent node (AC of #293).
 *
 * Member qualification (#317). A bare `module::owner::member` id collapsed distinct logical members onto one
 * node under the store's upsert:
 *   - a getter and setter of the SAME name (`get value()` + `set value(v)`), and
 *   - method OVERLOADS (`foo(x:number); foo(x:string); foo(x:any){}`).
 * We qualify only where it is needed to keep one-node-per-logical-member:
 *   - ACCESSORS carry their kind in the id: `${moduleId}::${ownerName}::value:get` vs `...::value:set`, so a
 *     get and a set of the same name are two DISTINCT nodes.
 *   - OVERLOADS are NOT given per-signature ids: every declaration of `foo` (the bodyless overload signatures
 *     and the implementation) maps to the same `${moduleId}::${ownerName}::foo`, and the caller de-duplicates
 *     to a single node (see collectMembers). Rationale (KISS): at this purely-syntactic tier a method is ONE
 *     logical member; arity/parameter-type discriminators only carry meaning once #294 resolves types, and
 *     emitting three "foo" nodes now would add ambiguity, not signal. The single node is anchored at the
 *     implementation (the body-bearing declaration).
 *
 * Collision note: the accessor suffix is separated by a single `:` inside the member segment, mirroring the
 * ratified example on #317. As with the rest of the scheme, freedom from collisions holds for identifier- and
 * ordinary-string-named members (the norm); a member whose literal name is itself `value:get` is the same
 * pathological case the `::`-segment scheme already tolerates elsewhere and is out of scope here.
 */

/** The module / JSComponent node id for a parsed file. */
function componentIdFor(filePath, componentName) {
  return `${filePath}::${componentName}`;
}

/** A top-level symbol (Class/Interface/Function/Enum/TypeAlias) id. */
function symbolIdFor(moduleId, name) {
  return `${moduleId}::${name}`;
}

/**
 * A member (Method/Property/constructor/accessor) id. `accessorKind` is 'get' or 'set' for accessors and is
 * appended as a `:kind` discriminator so a getter and setter of the same name get distinct ids; it is null/
 * omitted for every other member (methods — including overloads — properties, constructors).
 */
function memberIdFor(moduleId, ownerName, memberName, accessorKind) {
  const base = `${moduleId}::${ownerName}::${memberName}`;
  return accessorKind ? `${base}:${accessorKind}` : base;
}

module.exports = { componentIdFor, symbolIdFor, memberIdFor };
