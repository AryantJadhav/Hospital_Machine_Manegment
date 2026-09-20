import type { Place } from './locationSuggestions';

/**
 * The choices offered for the Block and Level/Floor of a new place.
 *
 * Whatever already exists comes first, so a second room on Level-2 of Block-A is
 * two clicks, then the common defaults that do not exist yet, then "Other" (type
 * a name) and "None". Picking a default or typing a new name creates that block or
 * floor at the same time, so nobody has to build the tree top-down before adding
 * the first ward.
 */

export type Choice = { value: string; label: string };

export type Picked =
  | { kind: 'existing'; id: number }
  | { kind: 'suggest'; name: string }
  | { kind: 'other' }
  | { kind: 'none' }
  | { kind: 'unset' };

export const DEFAULT_BLOCKS = ['Block-A', 'Block-B'];
export const DEFAULT_FLOORS = ['Level-1', 'Level-2', 'Level-3'];

const BUILDING = 30;
const FLOOR = 40;

const OTHER: Choice = { value: 'other', label: 'Other (type a name)' };
const NONE: Choice = { value: 'none', label: 'None' };

const same = (a: string, b: string) => a.trim().toLowerCase() === b.trim().toLowerCase();

function build(existing: Place[], defaults: string[]): Choice[] {
  const sorted = [...existing].sort((a, b) => a.name.localeCompare(b.name, undefined, { numeric: true }));

  return [
    ...sorted.map((p) => ({ value: `existing:${p.id}`, label: p.name })),
    ...defaults
      .filter((d) => !existing.some((p) => same(p.name, d)))
      .map((d) => ({ value: `suggest:${d}`, label: d })),
    OTHER,
    NONE,
  ];
}

/** Blocks are buildings: the places at that level anywhere in the tree. */
export function blockChoices(all: Place[]): Choice[] {
  return build(all.filter((p) => p.level === BUILDING), DEFAULT_BLOCKS);
}

/** Floors of the chosen block. With a new block or none, there are none yet: only the defaults. */
export function floorChoices(all: Place[], block: Picked): Choice[] {
  const under = block.kind === 'existing'
    ? all.filter((p) => p.level === FLOOR && p.parentId === block.id)
    : [];

  return build(under, DEFAULT_FLOORS);
}

export function parsePicked(value: string): Picked {
  if (value === 'other') return { kind: 'other' };
  if (value === 'none') return { kind: 'none' };
  if (value.startsWith('existing:')) return { kind: 'existing', id: Number(value.slice('existing:'.length)) };
  if (value.startsWith('suggest:')) return { kind: 'suggest', name: value.slice('suggest:'.length) };
  return { kind: 'unset' };
}

/**
 * Where a new block goes. Under the hospital's one site or organisation if there is
 * exactly one, so a tree that already has that shape stays tidy; at the top level
 * if there is none or several, because guessing between sites would be wrong.
 */
export function parentForNewBlock(all: Place[]): number | null {
  const roots = all.filter((p) => p.parentId === null && p.level <= 20);
  return roots.length === 1 ? roots[0].id : null;
}
