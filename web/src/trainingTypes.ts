/** What the training pages ask the server for. */

export type TrainingRow = {
  id: number;
  title: string;
  sessionDate: string;
  equipmentTypeId: number | null;
  equipmentTypeName: string | null;
  equipmentId: number | null;
  assetTag: string | null;
  machineName: string | null;
  manufacturer: string | null;
  model: string | null;
  trainer: string | null;
  venue: string | null;
  durationMinutes: number | null;
  attendeeCount: number;
  someAttendees: string[];
  isPlanned: boolean;
};

export type TrainingAttendee = {
  id: number;
  userId: number | null;
  name: string;
  designation: string | null;
};

/** The machine a session was on, as its own record has it. */
export type TrainingMachine = {
  id: number;
  assetTag: string;
  machineName: string | null;
  manufacturer: string | null;
  model: string | null;
  serialNumber: string | null;
  locationName: string | null;
};

export type TrainingDetail = {
  id: number;
  /** What the printed report calls it, e.g. TR-2026-00012. */
  reference: string;
  title: string;
  sessionDate: string;
  equipmentTypeId: number | null;
  equipmentTypeName: string | null;
  equipmentId: number | null;
  machine: TrainingMachine | null;
  trainer: string | null;
  venue: string | null;
  durationMinutes: number | null;
  notes: string | null;
  isPlanned: boolean;
  createdByName: string | null;
  createdAtUtc: string;
  attendees: TrainingAttendee[];
};

export type TrainingPerson = {
  userId: number | null;
  name: string;
  designation: string | null;
  sessions: number;
  lastSessionDate: string;
  covered: string[];
};

/** How long a session ran, in the words a person would use: 45 min, 1 h 30 min, 2 h. */
export function formatMinutes(minutes: number | null): string | null {
  if (minutes === null) return null;
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  if (h === 0) return `${m} min`;
  return m === 0 ? `${h} h` : `${h} h ${m} min`;
}
