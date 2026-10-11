#!/usr/bin/env node
// Hospital PM stress test: finds where it breaks.
//
// Vendor-side, like the other tools: CI never publishes tools/. Node 18+ only, no packages (it uses node:http).
//
// It ramps the number of simultaneous virtual users stage by stage. Every stage runs for a fixed time with
// each user sending requests back to back (no pauses unless --think-ms), and reports throughput, errors by kind
// and latency percentiles. A separate probe hits /health once a second on its own connection, so a server that
// is drowning is told apart from one that is merely slow. It stops at the first stage that breaks (too many
// errors, too slow, or the health probe failing), then watches whether the app RECOVERS once the load is gone.
//
//   node tools/stresstest/stress.mjs --url http://127.0.0.1:5600 --user admin --password '...'
//
// Point it only at an installation that is yours to break, with a throwaway database. See README.md.

import http from 'node:http';
import https from 'node:https';
import fs from 'node:fs';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

const run = promisify(execFile);

// ------------------------------------------------------------------ arguments
const args = Object.fromEntries(
  process.argv.slice(2).reduce((acc, a, i, all) => {
    if (a.startsWith('--')) acc.push([a.slice(2), all[i + 1] && !all[i + 1].startsWith('--') ? all[i + 1] : true]);
    return acc;
  }, []),
);

if (args.help || !args.url || !args.user || !args.password) {
  console.log(`Hospital PM stress test

  --url <base url>          required, e.g. http://127.0.0.1:5600
  --user <name>             required, a sign-in that may read everything (the Developer)
  --password <password>     required
  --stages <n,n,n>          concurrent users per stage        default 10,25,50,100,200,400,800,1500,3000
  --stage-seconds <s>       how long each stage runs          default 20
  --scenario <name>         read | heavy | write | login | mixed     default mixed
                              read   lists, searches, details, dashboard, reports (light to medium)
                              heavy  PDFs, label sheets, the full export (CPU and memory heavy)
                              write  reports faults, takes them, notes, resolves (CREATES DATA)
                              login  sign-ins only (password hashing is deliberately slow)
                              mixed  most of the above (default)
  --timeout-ms <ms>         a request slower than this is a failure         default 15000
  --max-error-rate <pct>    stage "breaks" above this share of failed requests   default 5
  --max-p95-ms <ms>         stage "breaks" when the 95th percentile exceeds this   default 10000
  --think-ms <ms>           pause between a user's requests       default 0 (flat out)
  --containers <a,b>        Docker containers to sample CPU and memory from
  --ssh <user@host>         run the Docker sampling on that machine instead of this one
  --out <folder>            where the JSON report goes            default tools/stresstest/results
  --keep-going              do not stop at the first broken stage
  --oversize                also send 2 MB fault descriptions (the app accepts them and keeps them: it pollutes the data)
`);
  process.exit(args.help ? 0 : 1);
}

const base = new URL(args.url);
const stages = String(args.stages ?? '10,25,50,100,200,400,800,1500,3000').split(',').map(Number);
const stageSeconds = Number(args['stage-seconds'] ?? 20);
const scenario = String(args.scenario ?? 'mixed');
const timeoutMs = Number(args['timeout-ms'] ?? 15000);
const maxErrorRate = Number(args['max-error-rate'] ?? 5);
const maxP95 = Number(args['max-p95-ms'] ?? 10000);
const thinkMs = Number(args['think-ms'] ?? 0);
const containers = args.containers ? String(args.containers).split(',') : [];
const outDir = String(args.out ?? path.join('tools', 'stresstest', 'results'));

// ------------------------------------------------------------------ http
const lib = base.protocol === 'https:' ? https : http;
const agent = new lib.Agent({ keepAlive: true, maxSockets: Infinity, maxFreeSockets: 4096 });

function request(method, pathAndQuery, { body, token, timeout = timeoutMs, useAgent = agent } = {}) {
  return new Promise((resolve) => {
    const started = performance.now();
    const payload = body === undefined ? null : typeof body === 'string' || Buffer.isBuffer(body) ? body : JSON.stringify(body);
    const headers = { accept: '*/*' };
    if (token) headers.authorization = `Bearer ${token}`;
    if (payload !== null) {
      headers['content-type'] = 'application/json';
      headers['content-length'] = Buffer.byteLength(payload);
    }

    let done = false;
    const finish = (r) => {
      if (done) return;
      done = true;
      resolve({ ms: performance.now() - started, ...r });
    };

    const req = lib.request(
      { host: base.hostname, port: base.port || (base.protocol === 'https:' ? 443 : 80), path: pathAndQuery, method, headers, agent: useAgent },
      (res) => {
        let bytes = 0;
        let text = '';
        // Only a JSON answer is read as text, whole (up to 1 MB): the checks need ids and tokens out of it. A PDF or a
        // zip is only counted.
        const readable = /json|text/i.test(String(res.headers['content-type'] ?? ''));
        res.on('data', (c) => {
          bytes += c.length;
          if (readable && text.length < 1_000_000) text += c.toString('utf8');
        });
        res.on('end', () => finish({ status: res.statusCode, bytes, text }));
        res.on('error', (e) => finish({ error: e.code ?? 'response-error' }));
      },
    );
    req.setTimeout(timeout, () => {
      finish({ error: 'timeout' });
      req.destroy();
    });
    req.on('error', (e) => finish({ error: e.code ?? e.message?.slice(0, 40) ?? 'error' }));
    if (payload !== null) req.write(payload);
    req.end();
  });
}

const json = (r) => {
  try {
    return JSON.parse(r.text);
  } catch {
    return null;
  }
};

// ------------------------------------------------------------------ sign-in (the token lasts 15 minutes)
let token = '';
let tokenAt = 0;
async function signIn() {
  const r = await request('POST', '/api/auth/login', { body: { userName: args.user, password: args.password }, timeout: 30000 });
  if (r.status !== 200) {
    console.error(`Could not sign in as ${args.user}: ${r.status ?? r.error}. ${r.text ?? ''}`);
    process.exit(2);
  }
  token = json(r).accessToken;
  tokenAt = Date.now();
}
const freshToken = async () => {
  if (Date.now() - tokenAt > 10 * 60 * 1000) await signIn();
  return token;
};

// ------------------------------------------------------------------ what is in the register, so ids are real
const facts = { equipmentIds: [], equipmentTags: [], workOrderIds: [], pmDoneTask: null, equipmentTotal: 0 };

async function discover() {
  const t = token;
  const eq = json(await request('GET', '/api/equipment?page=1&pageSize=200', { token: t }));
  facts.equipmentTotal = eq?.total ?? 0;
  for (const e of eq?.items ?? []) {
    facts.equipmentIds.push(e.id);
    facts.equipmentTags.push(e.assetTag);
  }
  const wo = json(await request('GET', '/api/work-orders?pageSize=25', { token: t }));
  for (const w of wo?.items ?? []) facts.workOrderIds.push(w.id);
  const done = json(await request('GET', '/api/pm/tasks?status=40&pageSize=1', { token: t }));
  facts.pmDoneTask = done?.items?.[0]?.id ?? null;
  if (!facts.equipmentIds.length || !facts.workOrderIds.length) {
    console.error(`Could not read a register to test against (${facts.equipmentIds.length} machines, ${facts.workOrderIds.length} service requests). Fill it first (tools/HospitalPm.DemoData), and check that the work orders list is a reasonable size.`);
    process.exit(2);
  }
}

const pick = (a) => a[Math.floor(Math.random() * a.length)];
const rnd = (n) => 1 + Math.floor(Math.random() * n);

// ------------------------------------------------------------------ the operations (weight, name, function)
const reads = [
  [10, 'dashboard', (t) => request('GET', '/api/dashboard', { token: t })],
  [18, 'equipment list', (t) => request('GET', `/api/equipment?page=${rnd(20)}&pageSize=25`, { token: t })],
  [10, 'equipment search', (t) => request('GET', `/api/equipment?q=${encodeURIComponent(pick(facts.equipmentTags).slice(0, 7))}`, { token: t })],
  [10, 'equipment detail', (t) => request('GET', `/api/equipment/${pick(facts.equipmentIds)}`, { token: t })],
  [10, 'work orders list', (t) => request('GET', `/api/work-orders?pageSize=25&page=${rnd(2)}`, { token: t })],
  [8, 'work order detail', (t) => request('GET', `/api/work-orders/${pick(facts.workOrderIds)}`, { token: t })],
  [10, 'pm tasks', (t) => request('GET', `/api/pm/tasks?pageSize=25&page=${rnd(10)}`, { token: t })],
  [5, 'pm summary', (t) => request('GET', '/api/pm/summary', { token: t })],
  [4, 'locations', (t) => request('GET', '/api/locations', { token: t })],
  [4, 'lookups', (t) => request('GET', '/api/lookups/equipment-types', { token: t })],
  [4, 'downtime report', (t) => request('GET', '/api/reports/downtime', { token: t })],
  [4, 'pm compliance report', (t) => request('GET', '/api/reports/pm-compliance', { token: t })],
  [3, 'health', () => request('GET', '/health')],
];

const heavy = [
  [30, 'work order PDF', (t) => request('GET', `/api/reports/work-orders/${pick(facts.workOrderIds)}/report.pdf`, { token: t })],
  [30, 'PM certificate PDF', (t) => (facts.pmDoneTask ? request('GET', `/api/reports/pm/${facts.pmDoneTask}/certificate.pdf`, { token: t }) : request('GET', '/api/dashboard', { token: t }))],
  [25, 'label sheet PDF', (t) => request('POST', '/api/labels/sheet', { token: t, body: { equipmentIds: facts.equipmentIds.slice(0, 24) } })],
  [15, 'full export (zip)', (t) => request('GET', '/api/admin/export', { token: t, timeout: Math.max(timeoutMs, 60000) })],
];

const writes = [
  [40, 'report a fault', async (t) => {
    const r = await request('POST', '/api/work-orders', { token: t, body: { equipmentId: pick(facts.equipmentIds), faultDescription: `stress test ${Date.now()}`, priority: 20 } });
    // Taken and resolved straight away half the time, so the status moves are exercised and the queue does not grow without end.
    if (r.status === 201 && Math.random() < 0.5) {
      const id = json(r)?.id;
      if (id) {
        await request('POST', `/api/work-orders/${id}/status`, { token: t, body: { status: 30 } });
        await request('POST', `/api/work-orders/${id}/notes`, { token: t, body: { body: 'stress note' } });
      }
    }
    return r;
  }],
  [20, 'add a note', (t) => request('POST', `/api/work-orders/${pick(facts.workOrderIds)}/notes`, { token: t, body: { body: 'stress note' } })],
  [10, 'bad input (400 expected)', (t) => request('POST', '/api/work-orders', { token: t, body: { equipmentId: 1, faultDescription: ' ', priority: 20 } })],
];

// Only with --oversize. The app has no limit on free text, so this is ACCEPTED and stored: it leaves service requests
// with a 2 MB description that every later list then carries. Kept out of the normal runs for that reason.
const oversize = [
  [10, 'huge body (400 or 413)', (t) => request('POST', '/api/work-orders', { token: t, body: { equipmentId: 1, faultDescription: 'x'.repeat(2_000_000), priority: 20 } })],
];
if (args.oversize) writes.push(...oversize);

const logins = [[1, 'sign-in', () => request('POST', '/api/auth/login', { body: { userName: args.user, password: args.password } })]];

const scenarios = {
  read: reads,
  heavy,
  write: writes,
  login: logins,
  mixed: [...reads.map(([w, n, f]) => [w * 3, n, f]), ...heavy.map(([w, n, f]) => [Math.ceil(w / 3), n, f]), ...writes.map(([w, n, f]) => [Math.ceil(w / 2), n, f]), ...logins],
};
const ops = scenarios[scenario];
if (!ops) {
  console.error(`Unknown scenario "${scenario}". Use read, heavy, write, login or mixed.`);
  process.exit(1);
}
const totalWeight = ops.reduce((s, o) => s + o[0], 0);
function chooseOp() {
  let x = Math.random() * totalWeight;
  for (const o of ops) {
    if ((x -= o[0]) <= 0) return o;
  }
  return ops[0];
}

// ------------------------------------------------------------------ measuring
const pct = (sorted, p) => (sorted.length ? sorted[Math.min(sorted.length - 1, Math.floor((p / 100) * sorted.length))] : 0);

// A request is a success when it is a 2xx/3xx, or when it was meant to be refused (the two bad-input tests, a PDF with
// no data). 5xx, timeouts and connection errors are failures, as is 429 or a 4xx nothing planned for.
function classify(r, opName) {
  if (r.error) return r.error === 'timeout' ? 'timeout' : `connection:${r.error}`;
  if (r.status >= 500) return `http ${r.status}`;
  if (opName.includes('400') && (r.status === 400 || r.status === 413)) return 'ok';
  // A note on a request that has been closed is refused (409) by design; that is not the app failing.
  if (opName.includes('note') && r.status === 409) return 'ok';
  if (r.status >= 400) return `http ${r.status}`;
  return 'ok';
}

async function sampleDocker() {
  if (!containers.length) return null;
  const fmt = '{{.Name}}|{{.CPUPerc}}|{{.MemUsage}}';
  try {
    const cmd = args.ssh ? ['ssh', ['-o', 'BatchMode=yes', String(args.ssh), `docker stats --no-stream --format '${fmt}' ${containers.join(' ')}`]] : ['docker', ['stats', '--no-stream', '--format', fmt, ...containers]];
    const { stdout } = await run(cmd[0], cmd[1], { timeout: 15000 });
    return stdout.trim().split('\n').map((l) => {
      const [name, cpu, mem] = l.split('|');
      return { name, cpu: parseFloat(cpu), mem: mem?.split('/')[0]?.trim() };
    });
  } catch {
    return null;
  }
}

const memToMb = (s) => {
  const m = /([\d.]+)\s*(KiB|MiB|GiB|B)/.exec(s ?? '');
  if (!m) return 0;
  return parseFloat(m[1]) * { B: 1 / 1048576, KiB: 1 / 1024, MiB: 1, GiB: 1024 }[m[2]];
};

async function runStage(users) {
  const outcomes = new Map();
  const perOp = new Map();
  const lat = [];
  const ok = [];
  let sent = 0;
  const health = { up: 0, down: 0, worstMs: 0 };
  const docker = {};
  const endAt = performance.now() + stageSeconds * 1000;
  let firstFailure = null;

  const probe = (async () => {
    // Its own agent and a fresh connection each time: a stuck connection pool must not hide a dead server.
    const lone = new lib.Agent({ keepAlive: false });
    while (performance.now() < endAt) {
      const r = await request('GET', '/health', { timeout: 3000, useAgent: lone });
      if (r.status === 200) {
        health.up++;
        health.worstMs = Math.max(health.worstMs, r.ms);
      } else {
        health.down++;
        health.worstMs = Math.max(health.worstMs, r.ms);
      }
      await new Promise((r2) => setTimeout(r2, 1000));
    }
  })();

  const sampler = (async () => {
    while (performance.now() < endAt) {
      const s = await sampleDocker();
      for (const c of s ?? []) {
        const d = (docker[c.name] ??= { cpu: 0, mem: 0 });
        d.cpu = Math.max(d.cpu, c.cpu || 0);
        d.mem = Math.max(d.mem, memToMb(c.mem));
      }
      await new Promise((r2) => setTimeout(r2, 2000));
    }
  })();

  const user = async () => {
    while (performance.now() < endAt) {
      const [, name, fn] = chooseOp();
      const t = await freshToken();
      const r = await fn(t);
      sent++;
      const kind = classify(r, name);
      outcomes.set(kind, (outcomes.get(kind) ?? 0) + 1);
      const o = perOp.get(name) ?? { n: 0, bad: 0, ms: [] };
      o.n++;
      if (kind !== 'ok') {
        o.bad++;
        firstFailure ??= { op: name, kind, atSeconds: stageSeconds - (endAt - performance.now()) / 1000 };
      }
      o.ms.push(r.ms);
      perOp.set(name, o);
      lat.push(r.ms);
      if (kind === 'ok') ok.push(r.ms);
      if (thinkMs) await new Promise((r2) => setTimeout(r2, thinkMs));
    }
  };

  await Promise.all([probe, sampler, ...Array.from({ length: users }, user)]);

  lat.sort((a, b) => a - b);
  ok.sort((a, b) => a - b);
  const failed = [...outcomes].filter(([k]) => k !== 'ok').reduce((s, [, n]) => s + n, 0);
  const slowest = [...perOp].map(([name, o]) => ({ name, n: o.n, bad: o.bad, p95: pct(o.ms.sort((a, b) => a - b), 95) })).sort((a, b) => b.p95 - a.p95);
  return {
    users,
    seconds: stageSeconds,
    requests: sent,
    perSecond: +(sent / stageSeconds).toFixed(1),
    failed,
    errorRatePct: sent ? +((failed / sent) * 100).toFixed(2) : 0,
    outcomes: Object.fromEntries(outcomes),
    p50: Math.round(pct(lat, 50)),
    p95: Math.round(pct(lat, 95)),
    p99: Math.round(pct(lat, 99)),
    max: Math.round(lat.at(-1) ?? 0),
    okP50: Math.round(pct(ok, 50)),
    health: { ...health, worstMs: Math.round(health.worstMs) },
    docker,
    slowest: slowest.slice(0, 3),
    firstFailure,
  };
}

function broke(s) {
  const reasons = [];
  if (s.errorRatePct > maxErrorRate) reasons.push(`${s.errorRatePct}% of requests failed (limit ${maxErrorRate}%)`);
  if (s.p95 > maxP95) reasons.push(`95th percentile ${s.p95} ms (limit ${maxP95} ms)`);
  if (s.health.down > s.health.up) reasons.push(`the health check failed ${s.health.down} of ${s.health.up + s.health.down} times`);
  return reasons;
}

// ------------------------------------------------------------------ go
await signIn();
await discover();

console.log(`\nHospital PM stress test  ${new Date().toISOString()}`);
console.log(`target ${base.origin}   scenario ${scenario}   ${stageSeconds}s per stage   timeout ${timeoutMs} ms`);
console.log(`register: ${facts.equipmentTotal} machines, ${facts.workOrderIds.length}+ service requests\n`);
console.log('users   req/s   requests   failed   err%    p50   p95    p99    max   health      CPU%/MB (max)');

const results = [];
let breakingPoint = null;

for (const users of stages) {
  const s = await runStage(users);
  results.push(s);
  const dock = Object.entries(s.docker).map(([n, d]) => `${n.slice(-10)} ${d.cpu.toFixed(0)}%/${d.mem.toFixed(0)}MB`).join('  ');
  console.log(
    `${String(users).padStart(5)} ${String(s.perSecond).padStart(7)} ${String(s.requests).padStart(10)} ${String(s.failed).padStart(8)} ${String(s.errorRatePct).padStart(5)} ${String(s.p50).padStart(6)} ${String(s.p95).padStart(5)} ${String(s.p99).padStart(6)} ${String(s.max).padStart(6)}   ${s.health.up}/${s.health.up + s.health.down} up   ${dock}`,
  );
  const why = broke(s);
  if (why.length && !breakingPoint) {
    breakingPoint = { users, why, firstFailure: s.firstFailure, outcomes: s.outcomes, slowest: s.slowest };
    if (!args['keep-going']) break;
  }
}

// ------------------------------------------------------------------ recovery: does it come back once the load is gone?
console.log('\nLoad removed. Waiting 15 seconds, then checking that it recovered...');
await new Promise((r) => setTimeout(r, 15000));
const lone = new lib.Agent({ keepAlive: false });
const after = [];
for (let i = 0; i < 5; i++) after.push(await request('GET', '/health', { timeout: 5000, useAgent: lone }));
let calm = [];
for (let i = 0; i < 20; i++) calm.push(await request('GET', '/api/dashboard', { token: await freshToken(), timeout: 10000 }));
const calmOk = calm.filter((r) => r.status === 200).length;
const calmP50 = Math.round(pct(calm.map((r) => r.ms).sort((a, b) => a - b), 50));
const healthy = after.every((r) => r.status === 200);
const recovered = healthy && calmOk === 20;
console.log(`  health after load: ${after.filter((r) => r.status === 200).length}/5 ok     dashboard: ${calmOk}/20 ok, median ${calmP50} ms   => ${recovered ? 'RECOVERED' : 'DID NOT RECOVER'}`);

// ------------------------------------------------------------------ verdict
console.log('\n================ RESULT ================');
if (breakingPoint) {
  console.log(`It broke at ${breakingPoint.users} simultaneous users.`);
  for (const w of breakingPoint.why) console.log(`  - ${w}`);
  console.log(`  first failure: "${breakingPoint.firstFailure?.op}" -> ${breakingPoint.firstFailure?.kind} after ${breakingPoint.firstFailure?.atSeconds.toFixed(1)} s`);
  console.log(`  what failed:   ${Object.entries(breakingPoint.outcomes).filter(([k]) => k !== 'ok').map(([k, n]) => `${k} x${n}`).join(', ')}`);
  console.log(`  slowest:       ${breakingPoint.slowest.map((o) => `${o.name} (p95 ${o.p95} ms)`).join(', ')}`);
  const last = results.filter((r) => !broke(r).length).at(-1);
  if (last) console.log(`The last stage that held: ${last.users} users, ${last.perSecond} requests/second, p95 ${last.p95} ms.`);
} else {
  console.log(`It did not break. The top stage (${stages.at(-1)} users) stayed within the limits. Raise --stages to push further.`);
}
console.log(`Recovery after the load: ${recovered ? 'yes' : 'NO'}`);

fs.mkdirSync(outDir, { recursive: true });
const file = path.join(outDir, `stress-${new Date().toISOString().replace(/[:.]/g, '-')}.json`);
fs.writeFileSync(file, JSON.stringify({ target: base.origin, scenario, stageSeconds, timeoutMs, facts: { machines: facts.equipmentTotal }, results, breakingPoint, recovery: { recovered, calmOk, calmP50 } }, null, 2));
console.log(`Report: ${file}`);
process.exit(0);
