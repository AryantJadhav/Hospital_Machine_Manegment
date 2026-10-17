/**
 * What each page is for, in words a nurse or a new engineer can follow. Shown by the Help button in the top bar, and
 * the words and the everyday jobs are on the Help page. Kept in one place so a page and its help are changed together.
 *
 * The first entry that matches the address wins, so the more specific ones come first.
 */
export type PageHelp = {
  test: RegExp;
  title: string;
  /** What the page is for, in a sentence or two. */
  purpose: string;
  /** What a person can do here, one short line each. */
  can: string[];
};

export const PAGE_HELP: PageHelp[] = [
  {
    test: /^\/pm\/[^/]+\/do/,
    title: 'Doing a planned maintenance',
    purpose: 'One check-up on one machine. Go through the checklist, answer each line, and save.',
    can: [
      'Answer every line. Say what you found, and add a note or a photo if something is wrong.',
      'Press Save when it is finished. The certificate is made for you.',
      'If it cannot be done, use Skip and say why. A skip is kept in the record.',
    ],
  },
  {
    test: /^\/pm/,
    title: 'Planned maintenance',
    purpose: 'The regular check-ups (PM) each machine is due for. Overdue ones are marked so they are not forgotten.',
    can: [
      'Open a task to do it, or to see what was done last time.',
      'Use the filters at the top to narrow the list.',
    ],
  },
  {
    test: /^\/work-orders\/\d+/,
    title: 'One service request',
    purpose: 'Everything about one fault, from the moment it was raised to the repair.',
    can: [
      'The engineer presses In progress when they reach the machine, and Resolved when it is fixed.',
      'Notes, parts used and photos are added while it is being worked on.',
      'Once it is resolved it goes back to the department that asked. They check the machine and answer: Accept closes it, and Not fixed sends it back to the engineer.',
      'Until then it is locked. The head of the department can also reopen it, or close it.',
    ],
  },
  {
    test: /^\/work-orders/,
    title: 'Request Service',
    purpose: 'Report a fault and follow every repair. The most urgent faults are listed first.',
    can: [
      'Press Report a fault, choose the machine, say what is wrong, and send it.',
      'Open any request to follow it. You get a request number to quote if you phone.',
      'The Response column says whether an engineer started in time.',
    ],
  },
  {
    test: /^\/service-history/,
    title: 'Service history',
    purpose: 'The repairs and maintenance that have been done, for a period you choose.',
    can: ['Pick the dates at the top.', 'Open any line to see the whole job.'],
  },
  {
    test: /^\/equipment\/\d+/,
    title: 'One machine',
    purpose: 'Everything about one machine: where it is, how old it is, its maintenance, repairs and moves.',
    can: [
      'Report a fault if something is wrong with it.',
      'Edit its details, move it to another room, or print its label.',
      'The machine stays on the register for good, even when it is retired. Nothing is deleted.',
    ],
  },
  {
    test: /^\/equipment/,
    title: 'Equipment register',
    purpose: 'Every machine in the hospital.',
    can: [
      'Search by asset tag, serial number or make. Use the filters to narrow it down.',
      'Open a machine to see its history. Add a machine with the button at the top.',
      'Tick machines to print their labels.',
    ],
  },
  {
    test: /^\/spare-parts/,
    title: 'Spare parts',
    purpose: 'The parts in stock. Parts that are low or out of stock are marked.',
    can: ['Parts used on a repair are recorded on that repair, and taken from stock for you.'],
  },
  {
    test: /^\/locations/,
    title: 'Locations',
    purpose: 'The buildings, departments and rooms where machines are kept.',
    can: ['Every machine belongs to a room. Add or rename places here.'],
  },
  {
    test: /^\/checklists/,
    title: 'Checklists',
    purpose: 'The step-by-step checks used in planned maintenance.',
    can: [
      'A checklist is written as a draft, then published.',
      'After it is published it can never be edited. A change is a new version, and old records keep the version they used.',
    ],
  },
  {
    test: /^\/incidents/,
    title: 'Incidents',
    purpose: 'Drops, falls and damage to machines, and what was found.',
    can: ['Anyone can write one up. The biomedical team looks into it, says what caused it, and closes it.'],
  },
  {
    test: /^\/gate-passes/,
    title: 'Gate passes',
    purpose: 'Machines going out of the hospital, for example to the maker for repair, and coming back.',
    can: ['Write a pass, print it, and record when the machine is back.'],
  },
  {
    test: /^\/training/,
    title: 'Training',
    purpose: 'The training sessions the department held, and who came.',
    can: ['Add a session, list the people who came, and print the sheet.'],
  },
  {
    test: /^\/reports/,
    title: 'Reports',
    purpose: 'Downtime, cost, work done, response times, training and spare-part stock, for a period you choose.',
    can: ['Pick the report at the top and the dates. Download any of them as a spreadsheet.'],
  },
  {
    test: /^\/staff/,
    title: 'Staff',
    purpose: 'Who can sign in, and what they may do.',
    can: ['People are never deleted. Someone who leaves is stopped from signing in, and can be let back in later.'],
  },
  {
    test: /^\/access/,
    title: 'Access',
    purpose: 'Give one person a permission their role does not have, or take one away.',
    can: ['Use this for the exceptions. A person\'s role gives them the rest.'],
  },
  {
    test: /^\/audit/,
    title: 'Audit log',
    purpose: 'A record of who changed what, and when. It cannot be edited.',
    can: ['Search it to find out who made a change.'],
  },
  {
    test: /^\/dashboard/,
    title: 'Today',
    purpose: 'How things stand right now: faults waiting, maintenance due, machines out of service.',
    can: ['Click any box to see the list behind it.'],
  },
];

const FALLBACK: PageHelp = {
  test: /.*/,
  title: 'This page',
  purpose: 'Use the menu at the top to move around. Each page has a short explanation under its title.',
  can: ['Press Help again on any page to see what it is for.'],
};

export function helpForPath(pathname: string): PageHelp {
  return PAGE_HELP.find((h) => h.test.test(pathname)) ?? FALLBACK;
}

/** The everyday jobs, step by step, for the Help page. */
export const HOW_DO_I: { title: string; steps: string[]; note?: string }[] = [
  {
    title: 'Report a fault',
    steps: [
      'Open Request Service and press Report a fault.',
      'Choose the machine: type its asset tag (it is on the label on the machine), or search for it.',
      'Write what is wrong in your own words.',
      'Press Send. You are given a request number. Keep it, to quote if you phone.',
    ],
  },
  {
    title: 'Work on a fault (engineers)',
    steps: [
      'Open Request Service and choose Assigned to me, to see the faults that are yours.',
      'Open the request. When you reach the machine, press In progress. The response time stops there.',
      'Add notes, the parts you used, and photos as you go.',
      'When it is fixed, write what was wrong and what you did under Solution, and press Resolved.',
    ],
    note: 'Once a request is resolved it is locked and goes back to the department that asked, to check it. If they say it is not fixed it comes back to you, with what is still wrong.',
  },
  {
    title: 'Do a planned maintenance',
    steps: [
      'Open Maintenance and choose a task that is due.',
      'Go through the checklist, answering every line.',
      'Add a note or photo for anything that is wrong.',
      'Press Save. If it cannot be done, use Skip and give the reason.',
    ],
  },
  {
    title: 'Add a machine',
    steps: [
      'Open Equipment and press Add a machine.',
      'Fill in the asset tag, the kind of machine and where it is. Dates, cost and expected life can be added now or later.',
      'Press Save. Print its label from the register and stick it on the machine.',
    ],
  },
  {
    title: 'Find a machine',
    steps: [
      'Open Equipment and type its asset tag, serial number or make in the search box.',
      'Or scan the label on the machine with a phone: it opens the machine\'s page.',
    ],
  },
  {
    title: 'Check a repair (the department that asked)',
    steps: [
      'When an engineer says a fault is fixed, Request Service shows a count beside its name and a message above the list.',
      'Open the request and read what was done. Check that the machine works.',
      'If it works, press Yes, it is fixed. The request is closed.',
      'If it does not, press No, it is not fixed and say what is still wrong. It goes straight back to the engineer.',
    ],
  },
  {
    title: 'Close or reopen a resolved fault (head of the department)',
    steps: [
      'Open the resolved request.',
      'To finish it, press Close. To say it was not really fixed, write why in the box and press Reopen.',
    ],
  },
];

/** The words the program uses, in everyday language. */
export const GLOSSARY: { term: string; meaning: string }[] = [
  { term: 'Planned maintenance (PM)', meaning: 'The regular check-up a machine is due for, to keep it safe and working, whether or not anything is wrong.' },
  { term: 'Service request, fault', meaning: 'A report that something is wrong with a machine. Also called a work order.' },
  { term: 'In progress', meaning: 'The engineer has reached the machine and started the work.' },
  { term: 'Resolved', meaning: 'The engineer says it is fixed. It goes back to the department that asked, to check. Until they answer it is locked.' },
  { term: 'Closed', meaning: 'The department accepted the repair (or the head of the department closed it). Nothing can be changed any more.' },
  { term: 'Response time', meaning: 'How long from a fault being raised until an engineer pressed In progress. It is not the time to fix it.' },
  { term: 'Priority', meaning: 'How urgent a fault is: Critical, High, Medium or Low. Critical faults are looked at first.' },
  { term: 'Criticality', meaning: 'How much a patient\'s care depends on a machine working. It says how closely the machine is looked after. It is never about a patient.' },
  { term: 'Asset tag', meaning: 'The number on the label stuck to a machine. It identifies that one machine.' },
  { term: 'Age and expected life', meaning: 'Age is counted from the day the machine was installed (or bought). Expected life is how many years it should stay in service.' },
  { term: 'Retired (condemned)', meaning: 'A machine taken out of service for good. It stays on the register so its history can still be read, but no more maintenance is planned for it.' },
  { term: 'Gate pass', meaning: 'The paper that lets a machine leave the hospital, for example to go to the maker for repair.' },
  { term: 'AMC', meaning: 'Annual maintenance contract: a vendor looks after the machine each year for a fee.' },
  { term: 'CMC', meaning: 'Comprehensive maintenance contract: like an AMC, and the parts are covered too.' },
  { term: 'Checklist', meaning: 'The list of checks done in a planned maintenance. Once published it cannot be edited; a change is a new version.' },
  { term: 'Audit log', meaning: 'The record of who changed what, and when. It cannot be edited.' },
];
