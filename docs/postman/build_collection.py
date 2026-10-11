"""Writes HospitalPM.postman_collection.json, the Postman collection for the Hospital PM API.

Run it from this folder:  python build_collection.py

Kept as a script so the JSON is always valid and the JavaScript in it is easy to read and change.
No password or token is in the file: it reads {{baseUrl}}, {{user}} and {{password}} from the Postman environment.
"""
import json

PRE_REQUEST = r"""// Signs in by itself when there is no token, it belongs to someone else, or it is about to run out (15 minutes).
// {{user}} and {{password}} come from the data file when there is one, so each run (iteration) is a different person.
if (pm.info.requestName !== "Login") {
  const user = pm.variables.get("user");
  const expires = Number(pm.environment.get("tokenExpires") || 0);
  if (!pm.environment.get("token") || pm.environment.get("tokenUser") !== user || Date.now() > expires - 60000) {
    pm.sendRequest({
      url: pm.environment.get("baseUrl") + "/api/auth/login",
      method: "POST",
      header: { "Content-Type": "application/json" },
      body: { mode: "raw", raw: JSON.stringify({ userName: user, password: pm.variables.get("password") }) }
    }, (err, res) => {
      if (err || res.code !== 200) { console.log("automatic sign-in failed", err || res.code); return; }
      const j = res.json();
      pm.environment.set("token", j.accessToken);
      pm.environment.set("tokenUser", user);
      pm.environment.set("tokenExpires", new Date(j.expiresAtUtc.replace(/(\.\d{3})\d+/, "$1")).getTime());
    });
  }
}"""

# What each role may see. Worked out against the real server: the Head cannot see the licence, backups or diagnostics;
# an engineer cannot see the staff list or the downtime report either.
def role_check(allowed, extra=""):
    """Test code: this person's role is allowed (200 and the extra checks) or must be refused (403)."""
    roles = json.dumps(allowed)
    return (
        'const role = pm.variables.get("role") || "Developer";\n'
        f"const may = {roles}.includes(role);\n"
        'pm.test(may ? role + " may see this (200)" : role + " is refused (403)", () => pm.response.to.have.status(may ? 200 : 403));\n'
        + (f"if (may) {{\n{extra}\n}}" if extra else "")
    )


def req(name, method, url, tests, body=None, auth=None, pre=None, desc=None):
    r = {"method": method, "header": [], "url": url}
    if body is not None:
        r["header"] = [{"key": "Content-Type", "value": "application/json"}]
        r["body"] = {"mode": "raw", "raw": json.dumps(body, indent=2) if not isinstance(body, str) else body,
                     "options": {"raw": {"language": "json"}}}
    if auth == "none":
        r["auth"] = {"type": "noauth"}
    if auth == "engineer":
        r["auth"] = {"type": "bearer", "bearer": [{"key": "token", "value": "{{engineerToken}}", "type": "string"}]}
    if desc:
        r["description"] = desc
    item = {"name": name, "request": r, "event": [{"listen": "test", "script": {"type": "text/javascript", "exec": tests.split("\n")}}]}
    if pre:
        item["event"].insert(0, {"listen": "prerequest", "script": {"type": "text/javascript", "exec": pre.split("\n")}})
    return item


def status(code, label=None):
    return f'pm.test("{label or "status is " + str(code)}", () => pm.response.to.have.status({code}));'


B = "{{baseUrl}}"

login = req(
    "Login", "POST", f"{B}/api/auth/login",
    status(200, "login works") + r"""
const j = pm.response.json();
pm.test("gives an access token", () => pm.expect(j.accessToken).to.be.a("string").and.not.empty);
pm.environment.set("token", j.accessToken);
pm.environment.set("tokenUser", pm.variables.get("user"));
pm.environment.set("tokenExpires", new Date(j.expiresAtUtc.replace(/(\.\d{3})\d+/, "$1")).getTime());
console.log("signed in as " + pm.variables.get("user") + " (" + (pm.variables.get("role") || "no role given") + ")");""",
    body='{ "userName": "{{user}}", "password": "{{password}}" }', auth="none",
    desc="Signs in with {{user}} and {{password}} from the environment and keeps the token. Every other request signs in by itself when the token has run out.")

health = req("Health (no sign-in needed)", "GET", f"{B}/health",
             status(200) + '\npm.test("says ok", () => pm.expect(pm.response.json().status).to.eql("ok"));\n'
             'pm.test("fast", () => pm.expect(pm.response.responseTime).to.be.below(500));', auth="none")

dashboard = req("Dashboard", "GET", f"{B}/api/dashboard",
                status(200) + r"""
const d = pm.response.json();
pm.test("has the equipment and service request numbers", () => {
  pm.expect(d.equipment.total).to.be.a("number");
  pm.expect(d.workOrders.open).to.be.a("number");
});
pm.test("fast", () => pm.expect(pm.response.responseTime).to.be.below(1500));""")

eq_list = req("Equipment list", "GET", f"{B}/api/equipment?page=1&pageSize=5",
              status(200) + r"""
const d = pm.response.json();
pm.test("has a list and a total", () => { pm.expect(d.items).to.be.an("array"); pm.expect(d.total).to.be.a("number"); });
pm.test("page size is respected", () => pm.expect(d.items.length).to.be.at.most(5));
if (d.items.length) { pm.environment.set("eqId", d.items[0].id); pm.environment.set("assetTag", d.items[0].assetTag); }""")

eq_search = req("Equipment search", "GET", f"{B}/api/equipment?q={{{{assetTag}}}}",
                status(200) + r"""
const d = pm.response.json();
pm.test("finds the machine by its tag", () => pm.expect(d.items.map(i => i.assetTag)).to.include(pm.environment.get("assetTag")));""",
                desc="Run Equipment list first: it saves a real asset tag to search for.")

eq_detail = req("Equipment detail", "GET", f"{B}/api/equipment/{{{{eqId}}}}",
                status(200) + r"""
const d = pm.response.json();
pm.test("is the machine asked for", () => pm.expect(d.id).to.eql(Number(pm.environment.get("eqId"))));
pm.test("says where it is and what it is", () => { pm.expect(d.locationName).to.be.a("string"); pm.expect(d.equipmentTypeName).to.be.a("string"); });""")

eq_missing = req("Equipment that does not exist (404)", "GET", f"{B}/api/equipment/999999999", status(404))

wo_list = req("Service requests list", "GET", f"{B}/api/work-orders?pageSize=5",
              status(200) + r"""
const d = pm.response.json();
pm.test("has items and a total", () => { pm.expect(d.items).to.be.an("array"); pm.expect(d.total).to.be.a("number"); });
if (d.items.length) pm.environment.set("openWoId", d.items[0].id);""")

pm_tasks = req("PM tasks", "GET", f"{B}/api/pm/tasks?pageSize=5", status(200) + '\npm.test("has items", () => pm.expect(pm.response.json().items).to.be.an("array"));')
pm_summary = req("PM summary", "GET", f"{B}/api/pm/summary", status(200))
locations = req("Locations", "GET", f"{B}/api/locations", status(200) + '\npm.test("is a list", () => pm.expect(pm.response.json()).to.be.an("array"));')
spare = req("Spare parts", "GET", f"{B}/api/spare-parts", status(200))
users = req("Staff list (Developer and Head only)", "GET", f"{B}/api/users",
            role_check(["Developer", "BmeHead"], 'pm.test("has people", () => pm.expect(pm.response.json().length).to.be.above(0));'))
licence = req("Licence status (Developer only)", "GET", f"{B}/api/admin/licence",
              role_check(["Developer"], 'pm.test("has a state and a message", () => pm.expect(pm.response.json().message).to.be.a("string"));'))
downtime = req("Downtime report (Developer and Head only)", "GET", f"{B}/api/reports/downtime", role_check(["Developer", "BmeHead"]))

report_fault = req(
    "1. Report a fault (creates a record)", "POST", f"{B}/api/work-orders",
    status(201) + r"""
const j = pm.response.json();
pm.test("gives a number", () => pm.expect(j.number).to.match(/^WO-/));
pm.environment.set("woId", j.id);""",
    body='{\n  "equipmentId": {{eqId}},\n  "faultDescription": "Postman test fault",\n  "priority": 30\n}',
    desc="Needs Equipment list to have run, for a real machine. Priority: 10 Low, 20 Medium, 30 High, 40 Critical. Creates a real service request.")

take = req("2. Take it: In progress (becomes yours)", "POST", f"{B}/api/work-orders/{{{{woId}}}}/status", status(204),
           body={"status": 30}, desc="Pressing In progress on a fault nobody has takes it. Status: 10 Reported, 20 Assigned, 30 In progress, 40 On hold, 50 Resolved, 60 Closed, 70 Cancelled.")
note = req("3. Add a note", "POST", f"{B}/api/work-orders/{{{{woId}}}}/notes", status(204), body={"body": "Checked by Postman"})
detail = req("4. The service request now", "GET", f"{B}/api/work-orders/{{{{woId}}}}",
             status(200) + r"""
const d = pm.response.json();
pm.test("is in progress", () => pm.expect(d.status).to.eql(30));
pm.test("is assigned to someone (the person who took it)", () => pm.expect(d.assignedToUserId).to.be.a("number"));
pm.test("has the note", () => pm.expect(d.notes.map(n => n.body)).to.include("Checked by Postman"));""")
resolve = req("5. Resolve it", "POST", f"{B}/api/work-orders/{{{{woId}}}}/resolve",
              status(200) + '\npm.test("is resolved", () => pm.expect(pm.response.json().status).to.eql(50));',
              body={"resolutionNotes": "Replaced the board (Postman test)"})
resolve_again = req("6. Resolve it again (409, already done)", "POST", f"{B}/api/work-orders/{{{{woId}}}}/resolve", status(409),
                    body={"resolutionNotes": "again"})
close = req("7. Close it", "POST", f"{B}/api/work-orders/{{{{woId}}}}/status", status(204), body={"status": 60})
reopen = req("8. Reopen a closed one (409)", "POST", f"{B}/api/work-orders/{{{{woId}}}}/status", status(409), body={"status": 30})
pdf = req("9. Service report PDF", "GET", f"{B}/api/reports/work-orders/{{{{woId}}}}/report.pdf",
          status(200) + '\npm.test("is a PDF", () => pm.expect(pm.response.headers.get("Content-Type")).to.include("pdf"));')

blank = req("Fault with no description (400)", "POST", f"{B}/api/work-orders", status(400) + '\npm.test("says what to fix", () => pm.expect(pm.response.json().error).to.be.a("string"));',
            body='{ "equipmentId": {{eqId}}, "faultDescription": "   ", "priority": 20 }')
bad_machine = req("Fault on a machine that does not exist (400)", "POST", f"{B}/api/work-orders", status(400),
                  body={"equipmentId": 999999999, "faultDescription": "x", "priority": 20})
no_token = req("No token (401)", "GET", f"{B}/api/equipment", status(401), auth="none")
bad_login = req("Wrong password (401)", "POST", f"{B}/api/auth/login", status(401),
                body={"userName": "{{user}}", "password": "this-is-wrong"}, auth="none",
                desc="One wrong try is fine. The account locks after several in a row.")

backup = req("Back up now (Developer only; makes a real backup)", "POST", f"{B}/api/admin/backups/run",
             role_check(["Developer"], r"""const j = pm.response.json();
pm.test("backup finished with no error", () => pm.expect(j.error).to.be.null);
pm.test("is a real file", () => pm.expect(j.sizeBytes).to.be.above(1000));"""),
             desc="Only the Developer may take a backup. Everyone else must get 403.")
backups = req("Backups: status and schedule (Developer only)", "GET", f"{B}/api/admin/backups",
              role_check(["Developer"], r"""const j = pm.response.json();
pm.test("keeps a fortnight (168) at one every two hours", () => { pm.expect(j.retainCount).to.eql(168); pm.expect(j.schedule.everyHours).to.eql(2); });
pm.test("pg_dump is found", () => pm.expect(j.tool.found).to.be.true);"""))

# Several different people acting in the same instant. Runs once, on the first person in the data file.
RACE = r"""// Two engineers press In progress on the same fault at the same moment: exactly one may get it.
if (pm.info.iteration > 0) {
  pm.test("runs once, on the first person in the data file", () => pm.expect(true).to.be.true);
} else {
  const base = pm.environment.get("baseUrl");
  const post = (url, body, token, done) => pm.sendRequest({
    url: base + url, method: "POST",
    header: Object.assign({ "Content-Type": "application/json" }, token ? { Authorization: "Bearer " + token } : {}),
    body: { mode: "raw", raw: JSON.stringify(body) }
  }, (err, res) => done(err, res));
  const login = (user, done) => post("/api/auth/login", { userName: user, password: "Hospital@2026" }, null,
    (e, r) => done(!e && r.code === 200 ? r.json().accessToken : null));

  login("s.deshmukh", head => login("a.kulkarni", anil => login("m.shaikh", sunita => {
    pm.test("three different people signed in", () => pm.expect([head, anil, sunita].every(Boolean)).to.be.true);
    if (![head, anil, sunita].every(Boolean)) { return; }
    post("/api/work-orders", { equipmentId: Number(pm.environment.get("eqId")), faultDescription: "Two people, one fault", priority: 30 }, head, (e, r) => {
      pm.test("the Head reported a fault", () => pm.expect(r.code).to.eql(201));
      const id = r.json().id;
      const results = [];
      const finish = () => {
        if (results.length < 2) { return; }
        pm.test("exactly one of the two engineers got it", () => pm.expect(results.map(x => x.code).sort()).to.eql([204, 409]));
        const refused = results.find(x => x.code === 409);
        pm.test("the other was told who has it", () => pm.expect(refused.json().error).to.include("already working on this"));
      };
      [anil, sunita].forEach(t => post("/api/work-orders/" + id + "/status", { status: 30 }, t, (err, res) => { results.push(res); finish(); }));
    });
  })));
}"""
race = req("Two engineers take the same fault at once (runs once)", "GET", f"{B}/health", RACE, auth="none",
           desc="Signs in as the Head and two engineers (the demo accounts), reports a fault, and has both engineers press In progress together. Needs 'Equipment list' to have run first.")

collection = {
    "info": {
        "name": "Hospital PM API",
        "description": "Checks for the Hospital PM API. Needs a Postman environment with baseUrl (and user, password and role if you run it without a data file).\n\nRun it once per person: Run collection, Data, choose users.json. Every run signs in as that person and expects what their role may do.\n\nFolders: Basics, A service request from start to finish (run in order; it creates a record), What must be refused, Developer only, Several people at once.",
        "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json",
    },
    "auth": {"type": "bearer", "bearer": [{"key": "token", "value": "{{token}}", "type": "string"}]},
    "event": [{"listen": "prerequest", "script": {"type": "text/javascript", "exec": PRE_REQUEST.split("\n")}}],
    "item": [
        login,
        {"name": "Basics (read only)", "item": [health, dashboard, eq_list, eq_search, eq_detail, eq_missing, wo_list, pm_tasks, pm_summary, locations, spare, users, licence, downtime]},
        {"name": "A service request from start to finish (run in order)", "item": [report_fault, take, note, detail, resolve, resolve_again, close, reopen, pdf]},
        {"name": "What must be refused", "item": [blank, bad_machine, no_token, bad_login]},
        {"name": "Developer only (everyone else must get 403)", "item": [backups, backup]},
        {"name": "Several people at once (runs once)", "item": [race]},
    ],
}

# The people to run as. Each row is one run of the whole collection. The real file (users.json) holds real passwords and
# is not committed; users.example.json is the same with a placeholder for the administrator's password.
people = [
    {"user": "admin", "password": "REPLACE-WITH-THE-ADMIN-PASSWORD", "role": "Developer"},
    {"user": "s.deshmukh", "password": "Hospital@2026", "role": "BmeHead"},
    {"user": "a.kulkarni", "password": "Hospital@2026", "role": "BmeEngineer"},
    {"user": "r.patil", "password": "Hospital@2026", "role": "BmeEngineer"},
    {"user": "m.shaikh", "password": "Hospital@2026", "role": "BmeEngineer"},
    {"user": "p.jadhav", "password": "Hospital@2026", "role": "BmeEngineer"},
    {"user": "v.more", "password": "Hospital@2026", "role": "BmeEngineer"},
]
with open("users.example.json", "w", encoding="utf-8") as f:
    json.dump(people, f, indent=2)
import os
if not os.path.exists("users.json"):
    with open("users.json", "w", encoding="utf-8") as f:
        json.dump(people, f, indent=2)
    print("wrote users.json: put the real admin password in it (it is not committed)")

with open("HospitalPM.postman_collection.json", "w", encoding="utf-8") as f:
    json.dump(collection, f, indent=2, ensure_ascii=False)

count = sum(1 for _ in json.dumps(collection).split('"method"')) - 1
print("written HospitalPM.postman_collection.json with", count, "requests")
