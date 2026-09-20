export type ItemType = 10 | 20 | 30 | 40 | 50;

export type Item = {
  key: string;
  label: string;
  type: ItemType;
  required: boolean;
  guidance?: string | null;
  unit?: string | null;
  min?: number | null;
  max?: number | null;
  options?: string[] | null;
};

/**
 * The control for one checklist question: pass/fail, yes/no, a choice, a reading or
 * free text. Shared by the PM form and the everyday check, so a value means the same
 * in both.
 */
export function AnswerControl({
  item,
  value,
  onChange,
}: {
  item: Item;
  value: string;
  onChange: (value: string) => void;
}) {
  // The same values the API validates and the Expo app sends. A third spelling
  // of "pass" would be a data problem, not a styling one.
  if (item.type === 10 || item.type === 20) {
    const options = item.type === 10
      ? [{ v: 'pass', label: 'Pass' }, { v: 'fail', label: 'Fail' }, { v: 'na', label: 'N/A' }]
      : [{ v: 'yes', label: 'Yes' }, { v: 'no', label: 'No' }, { v: 'na', label: 'N/A' }];

    return (
      <div role="group" aria-label={item.label} style={{ display: 'flex', gap: '0.4rem', flexWrap: 'wrap' }}>
        {options.map((o) => (
          <button
            key={o.v}
            type="button"
            aria-pressed={value === o.v}
            className={value === o.v ? 'btn btn-primary' : 'btn'}
            onClick={() => onChange(o.v)}
          >
            {o.label}
          </button>
        ))}
      </div>
    );
  }

  if (item.type === 50) {
    return (
      <div role="group" aria-label={item.label} style={{ display: 'flex', gap: '0.4rem', flexWrap: 'wrap' }}>
        {(item.options ?? []).map((o) => (
          <button
            key={o}
            type="button"
            aria-pressed={value === o}
            className={value === o ? 'btn btn-primary' : 'btn'}
            onClick={() => onChange(o)}
          >
            {o}
          </button>
        ))}
      </div>
    );
  }

  if (item.type === 30) {
    return (
      <div style={{ display: 'flex', gap: '0.4rem', alignItems: 'center' }}>
        <input
          className="field"
          aria-label={item.label}
          type="number"
          step="any"
          inputMode="decimal"
          value={value}
          onChange={(e) => onChange(e.target.value)}
          style={{ maxWidth: '12rem' }}
        />
        {item.unit && <span className="muted">{item.unit}</span>}
        {(item.min != null || item.max != null) && (
          <span className="muted">
            acceptable {item.min ?? '−∞'} to {item.max ?? '∞'}
          </span>
        )}
      </div>
    );
  }

  return (
    <input
      className="field"
      aria-label={item.label}
      value={value}
      maxLength={1000}
      onChange={(e) => onChange(e.target.value)}
    />
  );
}
