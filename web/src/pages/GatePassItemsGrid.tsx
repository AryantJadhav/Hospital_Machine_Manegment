import { useEffect, useRef } from 'react';
import type { ClipboardEvent, KeyboardEvent } from 'react';
import { blankRow, isBlank } from './gatePassRows';
import type { ItemRow } from './gatePassRows';

type Column = 'description' | 'assetCode' | 'quantity' | 'remarks';

const COLUMNS: Column[] = ['description', 'assetCode', 'quantity', 'remarks'];

/**
 * The items on a gate pass, filled in like the paper's table: a line for each, Description, Asset code,
 * Quantity and Remarks in columns. Enter moves down a column, and a block copied from Excel pastes into as
 * many lines as it has. A line that is a machine from the register keeps its number and its quantity of 1.
 */
export function GatePassItemsGrid({
  rows,
  onChange,
}: {
  rows: ItemRow[];
  onChange: (rows: ItemRow[]) => void;
}) {
  const cells = useRef(new Map<string, HTMLInputElement>());
  const focusNext = useRef<string | null>(null);

  // A line added by Enter or Add a line is focused once it has been drawn.
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
    focusNext.current = cellId(row.key, 'description');
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

  // Cells copied from Excel arrive as lines of tab-separated values. Spread them over the lines from this one down.
  function onPaste(e: ClipboardEvent<HTMLInputElement>, index: number, col: Column) {
    const text = e.clipboardData.getData('text');
    if (!/[\n\t]/.test(text)) return;
    e.preventDefault();

    const lines = text.replace(/\r/g, '').split('\n');
    if (lines[lines.length - 1] === '') lines.pop();

    const start = COLUMNS.indexOf(col);
    const next = [...rows];
    lines.forEach((line, i) => {
      const at = index + i;
      while (next.length <= at) next.push(blankRow());
      const row = { ...next[at] };
      line.split('\t').forEach((part, j) => {
        const target = COLUMNS[start + j];
        if (!target) return;
        // A machine from the register keeps its number and its quantity: only the words and remarks can be filled.
        if (row.equipmentId !== null && (target === 'assetCode' || target === 'quantity')) return;
        row[target] = part.trim();
      });
      next[at] = row;
    });
    onChange(next);
  }

  // A line nobody has written on is not an item, so its default quantity is not counted.
  const total = rows.reduce((sum, r) => {
    if (isBlank(r)) return sum;
    const q = Number(r.quantity);
    return sum + (Number.isInteger(q) && q > 0 ? q : 0);
  }, 0);

  const cell = (r: ReturnType<typeof blankRow>, i: number, col: Column, label: string, props: Record<string, unknown> = {}) => (
    <input
      ref={(el) => {
        if (el) cells.current.set(cellId(r.key, col), el);
        else cells.current.delete(cellId(r.key, col));
      }}
      aria-label={`${label}, line ${i + 1}`}
      value={r[col]}
      style={{ width: '100%' }}
      onChange={(e) => setCell(r.key, col, e.target.value)}
      onKeyDown={(e) => onKeyDown(e, i, col)}
      onPaste={(e) => onPaste(e, i, col)}
      {...props}
    />
  );

  return (
    <div className="stack" style={{ gap: '0.5rem' }}>
      <div className="table-wrap card" style={{ padding: 0 }}>
        <table className="table" aria-label="Items going out">
          <thead>
            <tr>
              <th style={{ width: '3.5rem' }}>Sr. No.</th>
              <th>Description</th>
              <th style={{ width: '10rem' }}>Asset code</th>
              <th style={{ width: '6rem' }}>Quantity</th>
              <th>Remarks</th>
              <th style={{ width: '5rem' }} aria-label="Remove" />
            </tr>
          </thead>
          <tbody>
            {rows.map((r, i) => {
              const machine = r.equipmentId !== null;
              return (
                <tr key={r.key}>
                  <td className="muted">{i + 1}</td>
                  <td>
                    {cell(r, i, 'description', 'Description', {
                      maxLength: 300,
                      placeholder: i === 0 ? 'e.g. Meniscus positioning device' : '',
                    })}
                    {machine && <div className="muted">From the register</div>}
                  </td>
                  <td>
                    {cell(r, i, 'assetCode', 'Asset code', {
                      maxLength: 100,
                      readOnly: machine,
                      placeholder: machine ? '' : 'NA',
                      className: machine ? 'mono' : undefined,
                    })}
                  </td>
                  <td>
                    {cell(r, i, 'quantity', 'Quantity', {
                      type: 'number',
                      min: 1,
                      max: 100000,
                      readOnly: machine,
                      inputMode: 'numeric',
                      // Typing over the 1 that is already there, as in a sheet.
                      onFocus: (e: { target: HTMLInputElement }) => e.target.select(),
                    })}
                  </td>
                  <td>
                    {cell(r, i, 'remarks', 'Remarks', {
                      maxLength: 500,
                      placeholder: i === 0 ? 'e.g. Not powering on' : '',
                    })}
                  </td>
                  <td>
                    <button
                      type="button"
                      className="btn btn-quiet"
                      aria-label={`Remove line ${i + 1}`}
                      onClick={() => removeRow(r.key)}
                    >
                      Remove
                    </button>
                  </td>
                </tr>
              );
            })}
          </tbody>
          <tfoot>
            <tr>
              <td colSpan={3} style={{ textAlign: 'right' }}><strong>Total</strong></td>
              <td><strong>{total.toLocaleString('en-IN')}</strong></td>
              <td colSpan={2} />
            </tr>
          </tfoot>
        </table>
      </div>

      <div className="row" style={{ alignItems: 'center' }}>
        <button type="button" className="btn" onClick={addRow}>Add a line</button>
      </div>

      <span className="muted">
        Press Enter to go to the next line, or paste a block of cells copied from Excel (Description, Asset code,
        Quantity, Remarks). Equipment only: this system holds no patient information.
      </span>
    </div>
  );
}
