import type { ReactNode } from 'react';
import type { Look, Tone } from './statusTones';

/**
 * A status, said three ways: a tone, an icon with its own silhouette, and a word.
 *
 * Colour alone is never enough. About one man in twelve cannot tell a red pill from
 * a green one, and a ward's lighting does the same to everyone at three in the
 * morning. The icons differ in shape: circle with a tick, triangle, octagon, circle
 * with an i, circle with a dash, circle with a pause.
 */

const ICONS: Record<string, ReactNode> = {
  success: (
    <>
      <circle cx="8" cy="8" r="6.25" />
      <path d="M5.2 8.2l2 2 3.6-4" />
    </>
  ),
  warning: (
    <>
      <path d="M8 2.2l6.2 11H1.8z" />
      <path d="M8 6.6v3M8 11.7v.1" />
    </>
  ),
  danger: (
    <>
      <path d="M5.2 1.8h5.6l3.4 3.4v5.6l-3.4 3.4H5.2l-3.4-3.4V5.2z" />
      <path d="M8 4.9v3.9M8 11.1v.1" />
    </>
  ),
  info: (
    <>
      <circle cx="8" cy="8" r="6.25" />
      <path d="M8 7.3v3.6M8 5.1v.1" />
    </>
  ),
  neutral: (
    <>
      <circle cx="8" cy="8" r="6.25" />
      <path d="M5.4 8h5.2" />
    </>
  ),
  pause: (
    <>
      <circle cx="8" cy="8" r="6.25" />
      <path d="M6.5 5.7v4.6M9.5 5.7v4.6" />
    </>
  ),
};

export function StatusIcon({ name }: { name: string }) {
  return (
    <svg
      className="ico"
      viewBox="0 0 16 16"
      width="14"
      height="14"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.75"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      {ICONS[name]}
    </svg>
  );
}

export function StatusPill({
  look,
  tone,
  className,
  children,
}: {
  /** From statusTones.ts. Or pass `tone` alone for a one-off. */
  look?: Look;
  tone?: Tone;
  className?: string;
  children: ReactNode;
}) {
  const resolved: Look = look ?? { tone: tone ?? 'neutral' };
  const icon = resolved.icon ?? resolved.tone;

  return (
    <span
      className={[
        'pill',
        `tone-${resolved.tone}`,
        resolved.strong ? 'tone-strong' : '',
        className ?? '',
      ]
        .filter(Boolean)
        .join(' ')}
    >
      <StatusIcon name={icon} />
      {children}
    </span>
  );
}
