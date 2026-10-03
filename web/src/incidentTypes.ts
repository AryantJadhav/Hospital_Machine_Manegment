/** What the incident pages ask the server for. */

import type { Look } from './statusTones';

/** What happened to the machine. The numbers are the server's. */
export const INCIDENT_TYPES: { value: number; label: string; help: string }[] = [
  { value: 10, label: 'Fall or drop', help: 'Dropped, knocked off a stand or table, or fell from a trolley.' },
  { value: 20, label: 'Mishandling', help: 'Rough, wrong or unauthorised handling or use.' },
  { value: 30, label: 'Liquid damage', help: 'A spill, a drip from above, or cleaning fluid got into it.' },
  { value: 40, label: 'Collision', help: 'Hit by a trolley, a bed, a door or another machine.' },
  { value: 50, label: 'Missing', help: 'Not where it should be, and not found.' },
  { value: 90, label: 'Other', help: 'Something else happened to it.' },
];

/** The state the machine was left in. */
export const DAMAGE_LEVELS: { value: number; label: string }[] = [
  { value: 10, label: 'No visible damage' },
  { value: 20, label: 'Minor damage, still works' },
  { value: 30, label: 'Major damage, needs repair' },
  { value: 40, label: 'Beyond repair' },
];

export type IncidentStatus = 'Reported' | 'InReview' | 'Closed';

export const INCIDENT_STATUS_LABEL: Record<IncidentStatus, string> = {
  Reported: 'Reported',
  InReview: 'Under review',
  Closed: 'Closed',
};

/** Reported is the one to act on; under review is work in motion; closed is done. */
export const INCIDENT_STATUS_LOOK: Record<IncidentStatus, Look> = {
  Reported: { tone: 'warning' },
  InReview: { tone: 'info' },
  Closed: { tone: 'neutral' },
};

/** How badly the machine was left, as a tone: the ones that cost the hospital a machine are the ones to notice. */
export const DAMAGE_LOOK: Record<number, Look> = {
  10: { tone: 'neutral' },
  20: { tone: 'warning' },
  30: { tone: 'danger' },
  40: { tone: 'danger', strong: true },
};

export type IncidentRow = {
  id: number;
  /** What is printed and quoted: INC-2026-00012. */
  reference: string;
  type: number;
  typeLabel: string;
  occurredOn: string;
  occurredAt: string | null;
  damage: number;
  damageLabel: string;
  status: IncidentStatus;
  statusLabel: string;
  takenOutOfUse: boolean;
  summary: string;
  equipmentId: number;
  assetTag: string;
  machineName: string | null;
  locationName: string | null;
};

export type IncidentCounts = { reported: number; inReview: number; closed: number };

export type IncidentDetail = {
  id: number;
  reference: string;
  type: number;
  typeLabel: string;
  occurredOn: string;
  occurredAt: string | null;
  place: string | null;
  description: string;
  involvedPerson: string | null;
  immediateAction: string | null;
  takenOutOfUse: boolean;
  damage: number;
  damageLabel: string;
  status: IncidentStatus;
  statusLabel: string;
  findings: string | null;
  correctiveAction: string | null;
  equipmentId: number;
  machine: {
    id: number;
    assetTag: string;
    machineName: string | null;
    manufacturer: string | null;
    model: string | null;
    serialNumber: string | null;
  } | null;
  locationName: string | null;
  reportedByName: string | null;
  reportedAtUtc: string;
  closedByName: string | null;
  closedAtUtc: string | null;
};

export type Tally = { label: string; value: number; number: number };

export type IncidentSummary = {
  total: number;
  open: number;
  closed: number;
  takenOutOfUse: number;
  byType: Tally[];
  byDamage: Tally[];
  byLocation: Tally[];
  repeatMachines: { equipmentId: number; assetTag: string; machineName: string | null; incidents: number }[];
};

/** What every incident form says about patients, once: this is about the machine. */
export const NO_PATIENTS =
  'This record is about the machine only. Do not write a patient’s name or details. If a patient was harmed, report it in the hospital’s own incident system.';
