import { useEffect, useRef, useState } from 'react';

/**
 * The text of a list page's search box, kept in step with the `q` in the page's address.
 *
 * What is typed is applied to the address a moment after the typing stops, so a keystroke is not a request.
 * And when the address changes by itself, as it does when the top bar's link to the bare list is clicked
 * while the page is showing a search, the box is emptied to match. Without that second half the page wrote
 * its own old text straight back into the address, and a search could not be cleared by going to the list.
 */
export function useSearchBox(q: string, apply: (typed: string) => void): [string, (typed: string) => void] {
  const [search, setSearch] = useState(q);

  // The last value this box put in the address, so a change to `q` that is not its own is recognised.
  const applied = useRef(q);
  const latest = useRef(apply);
  latest.current = apply;

  useEffect(() => {
    if (q !== applied.current) {
      applied.current = q;
      setSearch(q);
    }
  }, [q]);

  useEffect(() => {
    const typed = search.trim();
    if (typed === q) return;
    const t = window.setTimeout(() => {
      applied.current = typed;
      latest.current(typed);
    }, 300);
    return () => window.clearTimeout(t);
  }, [search, q]);

  return [search, setSearch];
}
