/**
 * Recovers an asset tag from a scanned payload.
 *
 * Mirrors AssetTagPayload.Extract on the server and the mobile app. Labels
 * encode a URL so a plain camera app opens the equipment page, but the tag
 * lives in the path — a label glued to a ventilator has to keep working
 * after somebody renames the server. Bare tags are accepted too, for labels
 * printed before this scheme and for hand-typed entry.
 */
export function extractAssetTag(scanned: string): string {
  const text = (scanned ?? '').trim();
  if (text.length === 0) return '';

  if (!/^https?:\/\//i.test(text)) return text;

  try {
    const url = new URL(text);
    const segments = url.pathname.split('/').filter(Boolean);

    if (segments.length >= 2 && segments[segments.length - 2].toLowerCase() === 'e') {
      return decodeURIComponent(segments[segments.length - 1]);
    }
    return text;
  } catch {
    return text;
  }
}
