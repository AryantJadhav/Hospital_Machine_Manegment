import { useRef, useState } from 'react';
import { formatBytes } from './bytes';

const MAX_PHOTOS = 10;
const MAX_PHOTO_BYTES = 10 * 1024 * 1024;
const PHOTO_ACCEPT = 'image/jpeg,image/png,image/webp,.jpg,.jpeg,.png,.webp';

/**
 * Choosing photos to attach, before they are saved. The files are checked here for size and number so a mistake is caught
 * before anything is sent; the server checks again, by what the bytes really are.
 *
 * Controlled: the parent holds the chosen files and saves them when it is ready (with the form, or on a button).
 */
export function PhotoPicker({
  files,
  onChange,
  alreadySaved = 0,
  label = 'Choose photos',
}: {
  files: File[];
  onChange: (files: File[]) => void;
  /** Photos the record already has, which count against the most it can hold. */
  alreadySaved?: number;
  label?: string;
}) {
  const [error, setError] = useState<string | null>(null);
  const input = useRef<HTMLInputElement>(null);

  function add(list: FileList | null) {
    if (!list || list.length === 0) return;
    const room = MAX_PHOTOS - alreadySaved - files.length;
    const next = [...files];
    let problem: string | null = null;

    for (const f of Array.from(list)) {
      if (f.size === 0) {
        problem = `"${f.name}" is empty.`;
        continue;
      }
      if (f.size > MAX_PHOTO_BYTES) {
        problem = `"${f.name}" is larger than 10 MB.`;
        continue;
      }
      if (next.length - files.length >= room) {
        problem = `At most ${MAX_PHOTOS} photos can be kept.`;
        break;
      }
      next.push(f);
    }

    setError(problem);
    onChange(next);
    if (input.current) input.current.value = '';
  }

  const full = alreadySaved + files.length >= MAX_PHOTOS;

  return (
    <div className="stack" style={{ gap: '0.5rem' }}>
      {!full && (
        <input
          ref={input}
          type="file"
          className="field"
          style={{ minWidth: 0, width: '100%' }}
          multiple
          accept={PHOTO_ACCEPT}
          aria-label={label}
          onChange={(e) => add(e.target.files)}
        />
      )}
      {files.length > 0 && (
        <ul className="stack" style={{ listStyle: 'none', margin: 0, padding: 0, gap: '0.25rem' }}>
          {files.map((f, i) => (
            <li key={`${f.name}-${i}`} className="row" style={{ justifyContent: 'space-between' }}>
              <span>{f.name} <span className="muted">· {formatBytes(f.size)}</span></span>
              <button
                type="button"
                className="btn btn-quiet"
                aria-label={`Remove ${f.name}`}
                onClick={() => onChange(files.filter((_, j) => j !== i))}
              >
                Remove
              </button>
            </li>
          ))}
        </ul>
      )}
      <p className="muted" style={{ margin: 0 }}>
        JPEG, PNG or WebP, up to 10 MB each. This system holds no patient information, so do not upload anything that shows a
        patient or a patient&apos;s details.
      </p>
      {error && <p className="alert alert-error" role="alert">{error}</p>}
    </div>
  );
}
