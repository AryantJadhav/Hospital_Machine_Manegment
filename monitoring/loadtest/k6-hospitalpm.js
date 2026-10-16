// Pilot load test for Hospital PM: signed-in people using the screens the way staff do, rising in steps to 1,000 at once.
//
// Run it only against a test or pilot copy that you are allowed to load (never a hospital's live system). It is started as
// a Kubernetes Job (job.yaml), which keeps the passwords in a Secret and sends its numbers to Prometheus so they sit beside
// the server's in Grafana. Settings come from the environment:
//
//   TARGET     http://hospitalpm.hospitalpm.svc:5000      where the application is
//   USERS      comma-separated sign-in names of test accounts (more than one, so it is not one account doing everything)
//   PASSWORD   their shared password (a test account's, from a Secret)
//   PROFILE    baseline | ramp (default) | soak
//   WRITES     1 to also report faults (creates data); 0 (default) is read-only
//
// People are not alike: each simulated person signs in once, then does one thing every 3 to 10 seconds (think time), as
// staff do. 1,000 of them is about 150 requests a second, not 1,000 at once flat out (that is what tools/stresstest does).
// No patient data exists in the product, so none is used here.
import http from 'k6/http';
import { check, sleep, group } from 'k6';
import { Counter, Rate } from 'k6/metrics';

const TARGET = (__ENV.TARGET || 'http://hospitalpm.hospitalpm.svc:5000').replace(/\/$/, '');
const USERS = (__ENV.USERS || '').split(',').map((s) => s.trim()).filter(Boolean);
const PASSWORD = __ENV.PASSWORD || '';
const WRITES = __ENV.WRITES === '1';
const PROFILE = __ENV.PROFILE || 'ramp';

if (USERS.length === 0 || !PASSWORD) {
  throw new Error('Set USERS (comma separated) and PASSWORD for the test accounts.');
}

const PROFILES = {
  // Is the system sound with a few people? Everything else is judged against this.
  baseline: [{ duration: '1m', target: 10 }, { duration: '4m', target: 10 }, { duration: '30s', target: 0 }],
  // Rises in steps, holds each, and ends with a rest so recovery can be seen.
  ramp: [
    { duration: '2m', target: 10 }, { duration: '3m', target: 10 },
    { duration: '2m', target: 50 }, { duration: '3m', target: 50 },
    { duration: '2m', target: 100 }, { duration: '3m', target: 100 },
    { duration: '3m', target: 250 }, { duration: '4m', target: 250 },
    { duration: '3m', target: 500 }, { duration: '4m', target: 500 },
    { duration: '3m', target: 750 }, { duration: '4m', target: 750 },
    { duration: '3m', target: 1000 }, { duration: '10m', target: 1000 },
    { duration: '1m', target: 0 }, { duration: '10m', target: 0 },
  ],
  // Steady at 250 for two hours: does anything leak or creep?
  soak: [{ duration: '5m', target: 250 }, { duration: '2h', target: 250 }, { duration: '2m', target: 0 }],
};

export const options = {
  scenarios: {
    people: { executor: 'ramping-vus', startVUs: 0, stages: PROFILES[PROFILE], gracefulRampDown: '30s' },
  },
  // What "good" means. A threshold that fails marks the run as failed; the report still says by how much.
  thresholds: {
    'http_req_failed{kind:read}': ['rate<0.01'],
    'http_req_duration{kind:read}': ['p(95)<2000', 'p(99)<5000'],
    checks: ['rate>0.99'],
    signin_failed: ['rate<0.02'],
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

const signinFailed = new Rate('signin_failed');
const sessionRenewed = new Counter('session_renewed');

const jsonHeaders = { 'Content-Type': 'application/json' };

function signIn(user) {
  const res = http.post(`${TARGET}/api/auth/login`, JSON.stringify({ userName: user, password: PASSWORD }), {
    headers: jsonHeaders, tags: { name: 'POST /api/auth/login', kind: 'signin' }, timeout: '60s',
  });
  const ok = check(res, { 'signed in': (r) => r.status === 200 });
  signinFailed.add(!ok);
  return ok ? res.json('accessToken') : null;
}

// Known good ids, found once before the people arrive.
export function setup() {
  const token = signIn(USERS[0]);
  if (!token) {
    throw new Error('Could not sign in as the first test account. Check USERS, PASSWORD and TARGET.');
  }
  const auth = { headers: { Authorization: `Bearer ${token}` } };
  const eq = http.get(`${TARGET}/api/equipment?page=1&pageSize=100`, auth).json('items') || [];
  const wo = http.get(`${TARGET}/api/work-orders?pageSize=50`, auth).json('items') || [];
  if (eq.length === 0) {
    throw new Error('The register is empty. Fill it with the demo data first (tools/HospitalPm.DemoData).');
  }
  return {
    equipmentIds: eq.map((e) => e.id),
    equipmentTags: eq.map((e) => e.assetTag).filter(Boolean),
    workOrderIds: wo.map((w) => w.id),
  };
}

const pick = (a) => a[Math.floor(Math.random() * a.length)];

// Each simulated person (VU) keeps its own sign-in between turns.
let token = null;
let tokenAt = 0;

// Every request is named by its pattern, not its address, so the number of series sent to Prometheus stays small.
function get(path, name, auth, expected = [200]) {
  return http.get(`${TARGET}${path}`, {
    ...auth, tags: { name: `GET ${name}`, kind: 'read' }, timeout: '30s',
    responseCallback: http.expectedStatuses(...expected),
  });
}

const ok200 = { ok: (r) => r.status === 200 };

export default function (data) {
  const user = USERS[(__VU - 1) % USERS.length];
  // Sign in once; renew before the 15-minute access token runs out.
  if (!token || Date.now() - tokenAt > 12 * 60 * 1000) {
    if (token) sessionRenewed.add(1);
    token = signIn(user);
    tokenAt = Date.now();
    if (!token) {
      sleep(5);
      return;
    }
  }
  const auth = { headers: { Authorization: `Bearer ${token}` } };

  // One thing a person does, drawn like real use: mostly looking, some searching, a little reading of reports.
  const roll = Math.random();
  if (roll < 0.20) {
    group('today', () => { check(get('/api/dashboard', '/api/dashboard', auth), ok200); });
  } else if (roll < 0.45) {
    check(get(`/api/equipment?page=${1 + Math.floor(Math.random() * 4)}&pageSize=25`, '/api/equipment', auth), ok200);
  } else if (roll < 0.55) {
    const q = encodeURIComponent((pick(data.equipmentTags) || 'a').slice(0, 6));
    check(get(`/api/equipment?q=${q}`, '/api/equipment?q', auth), ok200);
  } else if (roll < 0.70) {
    check(get(`/api/equipment/${pick(data.equipmentIds)}`, '/api/equipment/{id}', auth), ok200);
  } else if (roll < 0.80) {
    check(get('/api/work-orders?pageSize=25', '/api/work-orders', auth), ok200);
  } else if (roll < 0.86 && data.workOrderIds.length) {
    check(get(`/api/work-orders/${pick(data.workOrderIds)}`, '/api/work-orders/{id}', auth), ok200);
  } else if (roll < 0.94) {
    check(get('/api/pm/tasks?pageSize=25', '/api/pm/tasks', auth), ok200);
  } else if (roll < 0.97) {
    check(get('/api/pm/summary', '/api/pm/summary', auth), ok200);
  } else if (roll < 0.99) {
    // Engineers are not allowed to see this report: a 403 is the right answer for them, and is counted as one.
    check(get('/api/reports/pm-compliance', '/api/reports/pm-compliance', auth, [200, 403]), { 'ok or refused': (r) => r.status === 200 || r.status === 403 });
  } else if (WRITES) {
    // Reporting a fault: the one write people do all day. Creates a work order each time.
    const res = http.post(`${TARGET}/api/work-orders`, JSON.stringify({
      equipmentId: pick(data.equipmentIds), faultDescription: `Load test ${__VU}-${__ITER}`, priority: 20,
    }), { headers: { ...auth.headers, ...jsonHeaders }, tags: { name: 'POST /api/work-orders', kind: 'write' }, timeout: '30s' });
    check(res, { 'fault reported or refused': (r) => [200, 201, 400, 409].includes(r.status) });
  } else {
    check(get('/api/locations', '/api/locations', auth), ok200);
  }

  // Think time: a person reads the screen before the next click.
  sleep(3 + Math.random() * 7);
}
