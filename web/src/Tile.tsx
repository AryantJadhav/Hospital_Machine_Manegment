import { Link } from 'react-router-dom';
import { StatusIcon } from './StatusPill';

export type TileTone = 'ok' | 'warn' | 'danger';

// Zero is a quiet morning, not a success, so it has no tone and no word. Green is
// for a figure that is confirmed good.
const STATE_WORD = { danger: 'Act now', warn: 'Attention', ok: 'On track' } as const;
const STATE_ICON = { danger: 'danger', warn: 'warning', ok: 'success' } as const;

/**
 * One big number with its label. A tile that needs attention is tinted all over
 * and says so in words with an icon; a quiet one stays neutral. Links to the list
 * behind the number when given `to`.
 */
export function Tile({
  label,
  value,
  tone,
  to,
  hint,
}: {
  label: string;
  value: number | string;
  tone?: TileTone;
  to?: string;
  hint?: string;
}) {
  const body = (
    <>
      <span className="tile-value">{value}</span>
      <span className="tile-label">{label}</span>
      {tone && (
        <span className="tile-state">
          <StatusIcon name={STATE_ICON[tone]} />
          {STATE_WORD[tone]}
        </span>
      )}
      {hint && <span className="tile-hint">{hint}</span>}
    </>
  );

  const className = ['tile', tone ? `tile-${tone}` : ''].join(' ').trim();

  return to ? (
    <Link className={className} to={to}>{body}</Link>
  ) : (
    <div className={className}>{body}</div>
  );
}
