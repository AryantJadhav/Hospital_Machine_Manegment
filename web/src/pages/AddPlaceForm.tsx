import { useMemo, useState } from 'react';
import type { FormEvent } from 'react';
import { api } from '../api/client';
import { suggestCode, suggestNames } from '../locationSuggestions';
import type { Place } from '../locationSuggestions';
import { blockChoices, floorChoices, parentForNewBlock, parsePicked } from '../placeChoices';
import type { Picked } from '../placeChoices';

const BUILDING = 30;
const FLOOR = 40;

const PLACE_LEVELS = [
  { value: 50, label: 'Department' },
  { value: 60, label: 'Room' },
];

/**
 * Adding a ward or room the way people think of it: which Block, which Level/Floor,
 * then what it is called.
 *
 * Block and Level/Floor are lists (what already exists, a couple of common names,
 * Other to type one, None). Choosing a name that does not exist yet creates that
 * block or floor as part of the same step, so the first ward in a new hospital does
 * not mean building the tree from the top first.
 */
export function AddPlaceForm({
  all,
  onCancel,
  onSaved,
  onError,
  onUseFullForm,
}: {
  all: Place[];
  onCancel: () => void;
  onSaved: (message: string) => void | Promise<void>;
  onError: (msg: string | null) => void;
  /** For a site, an organisation, or a place inside something specific. */
  onUseFullForm: () => void;
}) {
  const [block, setBlock] = useState('');
  const [blockOther, setBlockOther] = useState('');
  const [floor, setFloor] = useState('');
  const [floorOther, setFloorOther] = useState('');
  const [level, setLevel] = useState(50);
  // The name is a list like the Block and the Level/Floor: the usual names for
  // this kind of place, then Other to type one that is not on it.
  const [nameChoice, setNameChoice] = useState('');
  const [nameOther, setNameOther] = useState('');
  const [typedCode, setTypedCode] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const pickedBlock = useMemo(() => parsePicked(block), [block]);
  const pickedFloor = useMemo(() => parsePicked(floor), [floor]);
  const pickedName = useMemo(() => parsePicked(nameChoice), [nameChoice]);
  const name = pickedName.kind === 'suggest' ? pickedName.name : pickedName.kind === 'other' ? nameOther : '';

  const blocks = useMemo(() => blockChoices(all), [all]);
  const floors = useMemo(() => floorChoices(all, pickedBlock), [all, pickedBlock]);

  // What the place will sit in, as far as it is known now, for the suggestions.
  const existingParent = useMemo(() => {
    const id = pickedFloor.kind === 'existing'
      ? pickedFloor.id
      : pickedBlock.kind === 'existing' ? pickedBlock.id : null;
    return id === null ? null : all.find((p) => p.id === id) ?? null;
  }, [all, pickedBlock, pickedFloor]);

  const nameSuggestions = useMemo(
    () => suggestNames(level, existingParent?.id ?? null, all),
    [level, existingParent, all],
  );

  // One name, or several at once: "ER, X-Ray, ECG" adds three places on the same level.
  const names = useMemo(
    () => [...new Set(name.split(/[,\n;]/).map((n) => n.trim()).filter(Boolean))],
    [name],
  );
  const several = names.length > 1;

  const code = typedCode ?? suggestCode(names[0] ?? '', existingParent, all);

  const blockName = blockNameOf(pickedBlock, blockOther, all);
  const floorName = floorNameOf(pickedFloor, floorOther, all);

  const missing =
    pickedBlock.kind === 'unset' || pickedFloor.kind === 'unset'
      ? 'Choose a Block and a Level/Floor, or None.'
      : (pickedBlock.kind === 'other' && !blockOther.trim())
        ? 'Type the name of the block.'
        : (pickedFloor.kind === 'other' && !floorOther.trim())
          ? 'Type the name of the level or floor.'
          : pickedName.kind === 'unset' || pickedName.kind === 'none'
            ? 'Choose a name, or Other to type one.'
            : names.length === 0
              ? 'Type the name.'
              : null;

  async function submit(e: FormEvent) {
    e.preventDefault();
    onError(null);
    if (missing) {
      onError(missing);
      return;
    }

    setBusy(true);
    // What exists as we go, so a code is unique against what was just created too.
    const known: Place[] = [...all];

    async function create(placeName: string, placeLevel: number, parentId: number | null): Promise<Place> {
      const parent = parentId === null ? null : known.find((p) => p.id === parentId) ?? null;
      const body = {
        code: suggestCode(placeName, parent, known) || placeName,
        name: placeName.trim(),
        level: placeLevel,
        parentId,
      };
      const made = await api.post<{ id: number }>('/api/locations', body);
      const place: Place = { id: made.id, code: body.code, name: body.name, level: placeLevel, parentId };
      known.push(place);
      return place;
    }

    // A name that already exists in the same place is that place, not a second one.
    const find = (n: string, lvl: number, parentId: number | null) =>
      known.find((p) => p.level === lvl && p.parentId === parentId && p.name.trim().toLowerCase() === n.trim().toLowerCase());

    try {
      let blockId: number | null = null;
      if (pickedBlock.kind === 'existing') {
        blockId = pickedBlock.id;
      } else if (pickedBlock.kind === 'suggest' || pickedBlock.kind === 'other') {
        const n = pickedBlock.kind === 'suggest' ? pickedBlock.name : blockOther;
        const top = parentForNewBlock(all);
        blockId = (find(n, BUILDING, top) ?? await create(n, BUILDING, top)).id;
      }

      let floorId: number | null = null;
      if (pickedFloor.kind === 'existing') {
        floorId = pickedFloor.id;
      } else if (pickedFloor.kind === 'suggest' || pickedFloor.kind === 'other') {
        const n = pickedFloor.kind === 'suggest' ? pickedFloor.name : floorOther;
        floorId = (find(n, FLOOR, blockId) ?? await create(n, FLOOR, blockId)).id;
      }

      const parentId = floorId ?? blockId;
      const parent = parentId === null ? null : known.find((p) => p.id === parentId) ?? null;

      const added: string[] = [];
      const already: string[] = [];

      for (const n of names) {
        if (find(n, level, parentId)) {
          already.push(n);
          continue;
        }

        // A code typed by hand is for one place; several get one each from their names.
        const finalCode = !several && typedCode ? typedCode : suggestCode(n, parent, known) || n;
        const made = await api.post<{ id: number }>('/api/locations', { code: finalCode, name: n, level, parentId });
        known.push({ id: made.id, code: finalCode, name: n, level, parentId });
        added.push(n);
      }

      if (added.length === 0) {
        onError(`Already there: ${already.join(', ')}.`);
        return;
      }

      const where = [blockName, floorName].filter(Boolean).join(', ');
      await onSaved(
        `Added ${list(added)}${where ? ` in ${where}` : ''}.`
          + (already.length > 0 ? ` ${list(already)} ${already.length === 1 ? 'was' : 'were'} already there.` : ''),
      );
    } catch (err) {
      onError(
        err instanceof Error
          ? `${err.message} Anything created before the problem is already in the list below.`
          : 'Could not save the place.',
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="card stack" onSubmit={submit}>
      <h2 style={{ margin: 0, fontSize: '1.05rem' }}>Add a place</h2>

      <div className="filters">
        <label className="field">
          <span>Block</span>
          <select aria-label="Block" value={block} onChange={(e) => { setBlock(e.target.value); setFloor(''); setNameChoice(''); }} required>
            <option value="">Choose…</option>
            {blocks.map((c) => (
              <option key={c.value} value={c.value}>{c.label}</option>
            ))}
          </select>
        </label>

        {pickedBlock.kind === 'other' && (
          <label className="field">
            <span>Block name</span>
            <input
              value={blockOther}
              onChange={(e) => setBlockOther(e.target.value)}
              placeholder="For example Main Block"
              autoComplete="off"
              required
            />
          </label>
        )}

        <label className="field">
          <span>Level / Floor</span>
          <select aria-label="Level or floor" value={floor} onChange={(e) => { setFloor(e.target.value); setNameChoice(''); }} required>
            <option value="">Choose…</option>
            {floors.map((c) => (
              <option key={c.value} value={c.value}>{c.label}</option>
            ))}
          </select>
        </label>

        {pickedFloor.kind === 'other' && (
          <label className="field">
            <span>Level / Floor name</span>
            <input
              value={floorOther}
              onChange={(e) => setFloorOther(e.target.value)}
              placeholder="For example Ground Floor"
              autoComplete="off"
              required
            />
          </label>
        )}
      </div>

      <div className="filters">
        <label className="field">
          <span>Is a</span>
          <select
            aria-label="Kind of place"
            value={level}
            onChange={(e) => { setLevel(Number(e.target.value)); setNameChoice(''); }}
          >
            {PLACE_LEVELS.map((l) => (
              <option key={l.value} value={l.value}>{l.label}</option>
            ))}
          </select>
        </label>

        <label className="field">
          <span>Name</span>
          <select aria-label="Name" value={nameChoice} onChange={(e) => setNameChoice(e.target.value)} required>
            <option value="">Choose…</option>
            {nameSuggestions.map((n) => (
              <option key={n} value={`suggest:${n}`}>{n}</option>
            ))}
            <option value="other">Other (type a name)</option>
          </select>
        </label>

        {pickedName.kind === 'other' && (
          <label className="field grow">
            <span>Name of the place</span>
            <input
              value={nameOther}
              onChange={(e) => setNameOther(e.target.value)}
              placeholder="One name, or several with commas: ER, X-Ray, ECG"
              autoComplete="off"
              required
            />
          </label>
        )}

        <label className="field">
          <span>Code</span>
          <input
            value={several ? '' : code}
            onChange={(e) => setTypedCode(e.target.value)}
            disabled={several}
            placeholder={several ? 'Made from each name' : undefined}
            required={!several}
          />
        </label>
      </div>

      <p className="muted" style={{ margin: 0, fontSize: '0.85rem' }}>
        Choosing a block or level that does not exist yet creates it too. The code follows the name until you type your own.
      </p>

      <div className="row">
        <button className="btn btn-primary" type="submit" disabled={busy}>
          {busy ? 'Adding…' : 'Add place'}
        </button>
        <button className="btn" type="button" onClick={onCancel}>Cancel</button>
        <button className="btn btn-quiet" type="button" onClick={onUseFullForm}>
          A site or organisation, or a place inside something specific
        </button>
      </div>
    </form>
  );
}

/** "ER, X-Ray and ECG". */
function list(items: string[]): string {
  return items.length < 2 ? items.join('') : `${items.slice(0, -1).join(', ')} and ${items[items.length - 1]}`;
}

function blockNameOf(p: Picked, other: string, all: Place[]): string {
  if (p.kind === 'existing') return all.find((x) => x.id === p.id)?.name ?? '';
  if (p.kind === 'suggest') return p.name;
  if (p.kind === 'other') return other.trim();
  return '';
}

function floorNameOf(p: Picked, other: string, all: Place[]): string {
  return blockNameOf(p, other, all);
}
