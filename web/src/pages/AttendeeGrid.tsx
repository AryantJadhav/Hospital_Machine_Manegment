import { useEffect, useRef } from 'react';
import type { ClipboardEvent, KeyboardEvent } from 'react';
import { blankRow, newKey } from './attendeeRows';
import type { AttendeeRow } from './attendeeRows';

type Staff = { id: number; fullName: string };

type Column = 'name' | 'designation';

/**
 * Who attended, filled in like a sheet: a row for each person, Name and Job in columns.
 * Enter moves down a column, and a block copied from Excel pastes into as many rows as it has.
 * Staff with an account can be picked from a list instead of typed.
 */
export function AttendeeGrid({
  rows,
  onChange,
  staff,
}: {
  rows: AttendeeRow[];
  onChange: (rows: AttendeeRow[]) => void;
  staff: Staff[];
}) {
  const cells = useRef(new Map<string, HTMLInputElement>());
  const focusNext = useRef<string | null>(null);

  // A row added by Enter or Add row is focused once it has been drawn.
  useEffect(() => {
    if (focusNext.current) {
      cells.current.get(focusNext.current)?.focus();
      focusNext.current = null;
    }
  });

  const cellId = (key: number, col: Column) => `${key}:${col}`;

  function setCell(key: number, col: Column, value: string) {
    onChange(rows.map((r) => (r.key === key ? { ...r, [col]: value } : r)));
  }

  function addRow() {
    const row = blankRow();
    focusNext.current = cellId(row.key, 'name');
    onChange([...rows, row]);
  }

  function removeRow(key: number) {
    const left = rows.filter((r) => r.key !== key);
    onChange(left.length > 0 ? left : [blankRow()]);
  }

  function onKeyDown(e: KeyboardEvent<HTMLInputElement>, index: number, col: Column) {
    if (e.key !== 'Enter') return;
    // Enter moves down the column, as in a sheet, rather than submitting the form.
    e.preventDefault();
    const next = rows[index + 1];
    if (next) {
      cells.current.get(cellId(next.key, col))?.focus();
    } else {
      const row = blankRow();
      focusNext.current = cellId(row.key, col);
      onChange([...rows, row]);
    }
  }

  // Cells copied from Excel arrive as lines of tab-separated values. Spread them over the rows from this one down.
  function onPaste(e: ClipboardEvent<HTMLInputElement>, index: number, col: Column) {
    const text = e.clipboardData.getData('text');
    if (!/[\n\t]/.test(text)) return;
    e.preventDefault();

    const lines = text.replace(/\r/g, '').split('\n');
    if (lines[lines.length - 1] === '') lines.pop();

    const next = [...rows];
    lines.forEach((line, i) => {
      const parts = line.split('\t');
      const at = index + i;
      while (next.length <= at) next.push(blankRow());
      const row = { ...next[at] };
      parts.forEach((part, j) => {
        const target: Column | null = col === 'name' ? (j === 0 ? 'name' : j === 1 ? 'designation' : null) : j === 0 ? 'designation' : null;
        // A staff member's name stays theirs; only their job can be filled.
        if (target === 'name' && row.userId !== null) return;
        if (target) row[target] = part.trim();
      });
      next[at] = row;
    });
    onChange(next);
  }

  const listed = new Set(rows.map((r) => r.userId).filter((id): id is number => id !== null));
  const available = staff.filter((s) => !listed.has(s.id));

  function addStaff(id: number) {
    const person = staff.find((s) => s.id === id);
    if (!person) return;
    const row: AttendeeRow = { key: newKey(), userId: person.id, name: person.fullName, designation: '' };
    // Fill the first empty line rather than leaving a gap above the new one.
    const empty = rows.findIndex((r) => r.userId === null && r.name.trim() === '' && r.designation.trim() === '');
    const next = [...rows];
    if (empty >= 0) next[empty] = row;
    else next.push(row);
    focusNext.current = cellId(row.key, 'designation');
    onChange(next);
  }

  const lower = rows.map((r) => r.name.trim().toLowerCase());
  const filled = rows.filter((r) => r.userId !== null || r.name.trim() !== '').length;

  return (
    <div className="stack" style={{ gap: '0.5rem' }}>
      <div className="table-wrap card" style={{ padding: 0 }}>
        <table className="table" aria-label="People who attended">
          <thead>
            <tr>
              <th style={{ width: '3.5rem' }}>No.</th>
              <th>Name</th>
              <th>Job</th>
              <th style={{ width: '5rem' }} aria-label="Remove" />
            </tr>
          </thead>
          <tbody>
            {rows.map((r, i) => {
              const dup = r.name.trim() !== '' && lower.indexOf(r.name.trim().toLowerCase()) !== i;
              return (
                <tr key={r.key}>
                  <td className="muted">{i + 1}</td>
                  <td>
                    <input
                      ref={(el) => {
                        if (el) cells.current.set(cellId(r.key, 'name'), el);
                        else cells.current.delete(cellId(r.key, 'name'));
                      }}
                      aria-label={`Name, row ${i + 1}`}
                      aria-invalid={dup || undefined}
                      value={r.name}
                      readOnly={r.userId !== null}
                      maxLength={200}
                      placeholder={i === 0 ? 'e.g. Meera Nair' : ''}
                      style={{ width: '100%', ...(dup ? { borderColor: 'var(--err)' } : {}) }}
                      onChange={(e) => setCell(r.key, 'name', e.target.value)}
                      onKeyDown={(e) => onKeyDown(e, i, 'name')}
                      onPaste={(e) => onPaste(e, i, 'name')}
                    />
                    {r.userId !== null && <div className="muted">Has an account</div>}
                    {dup && <div className="muted" role="alert">Already listed.</div>}
                  </td>
                  <td>
                    <input
                      ref={(el) => {
                        if (el) cells.current.set(cellId(r.key, 'designation'), el);
                        else cells.current.delete(cellId(r.key, 'designation'));
                      }}
                      aria-label={`Job, row ${i + 1}`}
                      value={r.designation}
                      maxLength={200}
                      placeholder={i === 0 ? 'e.g. Staff nurse, ICU' : ''}
                      style={{ width: '100%' }}
                      onChange={(e) => setCell(r.key, 'designation', e.target.value)}
                      onKeyDown={(e) => onKeyDown(e, i, 'designation')}
                      onPaste={(e) => onPaste(e, i, 'designation')}
                    />
                  </td>
                  <td>
                    <button
                      type="button"
                      className="btn btn-quiet"
                      aria-label={`Remove row ${i + 1}`}
                      onClick={() => removeRow(r.key)}
                    >
                      Remove
                    </button>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      <div className="row" style={{ alignItems: 'center' }}>
        <button type="button" className="btn" onClick={addRow}>Add a row</button>

        {available.length > 0 && (
          <label className="row" style={{ gap: '0.5rem', alignItems: 'center' }}>
            <span className="muted">Or pick staff with an account</span>
            <select
              aria-label="Add a staff member"
              value=""
              onChange={(e) => addStaff(Number(e.target.value))}
            >
              <option value="">Choose…</option>
              {available.map((s) => (
                <option key={s.id} value={s.id}>{s.fullName}</option>
              ))}
            </select>
          </label>
        )}

        <span className="muted" style={{ marginLeft: 'auto' }}>
          {filled} {filled === 1 ? 'person' : 'people'}
        </span>
      </div>

      <span className="muted">
        Press Enter to go to the next row, or paste a block of cells copied from Excel (Name, then Job).
        Staff only: this system holds no patient information, so do not enter a patient&apos;s name.
      </span>
    </div>
  );
}
