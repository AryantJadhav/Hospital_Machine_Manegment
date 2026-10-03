/** What the access screens ask the server for. */

/** One section that can be given to a person or taken from them. */
export type CatalogItem = {
  permission: string;
  group: string;
  label: string;
  help: string;
};

export type AccessEffect = 'Grant' | 'Revoke';

export type AccessGrant = {
  permission: string;
  effect: AccessEffect;
  expiresOn: string | null;
  note: string | null;
  /** The last day has passed, so it no longer does anything. */
  expired: boolean;
  grantedByName: string | null;
  grantedAtUtc: string;
};

export type AccessView = {
  user: { id: number; userName: string; fullName: string; role: string | null; isActive: boolean };
  /** What the person's role gives them. */
  rolePermissions: string[];
  grants: AccessGrant[];
  /** The sum: the role, plus what is given, minus what is taken away. */
  effective: string[];
  /** Why the access cannot be changed, or null when it can. */
  locked: string | null;
};

/** The groups in the order a person reads them. */
export const GROUP_ORDER = ['Looking', 'Everyday work', 'The register', 'The department', 'The installation'];

export function groupCatalog(catalog: CatalogItem[]): { group: string; items: CatalogItem[] }[] {
  const groups = new Map<string, CatalogItem[]>();
  for (const item of catalog) {
    groups.set(item.group, [...(groups.get(item.group) ?? []), item]);
  }
  return [...groups.entries()]
    .sort(([a], [b]) => GROUP_ORDER.indexOf(a) - GROUP_ORDER.indexOf(b))
    .map(([group, items]) => ({ group, items }));
}
