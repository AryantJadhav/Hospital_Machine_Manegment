import { useEffect } from 'react';

const PRODUCT = 'Hospital PM';

/** What each page is called in the browser tab and in a screen reader's page list. */
const TITLES: [prefix: string, title: string][] = [
  ['/dashboard', 'Today'],
  ['/pm', 'Preventive maintenance'],
  ['/work-orders', 'Work orders'],
  ['/equipment', 'Equipment'],
  ['/equipment-types', 'Equipment types'],
  ['/locations', 'Locations'],
  ['/checklists', 'Checklists'],
  ['/spare-parts', 'Spare parts'],
  ['/compliance', 'Compliance report'],
  ['/import', 'Import'],
  ['/export', 'Export data'],
  ['/staff', 'Staff'],
  ['/backups', 'Backups'],
  ['/diagnostics', 'Diagnostics'],
  ['/licence', 'Licence'],
  ['/updates', 'Updates'],
];

export function titleForPath(pathname: string): string {
  const hit = TITLES.find(([prefix]) => pathname === prefix || pathname.startsWith(`${prefix}/`));
  return hit ? hit[1] : '';
}

/**
 * Sets the tab title to "<page> · Hospital PM".
 *
 * It was "web" on every page - the name of the folder the project was scaffolded
 * in - so a person with several tabs open, or a screen reader announcing a page
 * change, was told nothing. Pass undefined to leave the title alone.
 */
export function usePageTitle(page: string | undefined) {
  useEffect(() => {
    if (page === undefined) return;
    document.title = page ? `${page} · ${PRODUCT}` : PRODUCT;
  }, [page]);
}
