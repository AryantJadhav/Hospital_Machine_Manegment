using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.DemoData;

// Fills a Hospital PM installation with a plausible hospital.
//
// Vendor-side, like the licence tool: CI never publishes tools/, so this
// cannot reach a customer. It drives the product's own public API rather than
// writing to the database, which means it can only create states the product
// itself can create — a seeder that reaches past the API will happily build a
// demo that the real thing could never produce.
//
// Written to be run against a throwaway install before showing the product to
// someone. It refuses to run against an installation that already holds real
// work, because "populate the demo" typed at the wrong window should not be
// how a hospital loses its register.
//
//   dotnet run --project tools/HospitalPm.DemoData -- \
//       --url http://localhost:5000 --user admin --password <password>

var url = Arg("--url") ?? "http://localhost:5000";
var userName = Arg("--user") ?? "admin";
var password = Arg("--password");
var force = args.Contains("--force", StringComparer.Ordinal);

if (password is null)
{
    Console.Error.WriteLine("""
        Hospital PM demo data

          --url       <base url>    default http://localhost:5000
          --user      <username>    default admin
          --password  <password>    required
          --force                   populate even if the install already has data

        Creates a hospital: locations, staff, equipment, checklists, a PM
        programme with history, work orders and a backup.
        """);
    return 1;
}

using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromMinutes(5) };

Console.WriteLine($"==> {url}");

// --- Sign in ---------------------------------------------------------------
var login = await http.PostAsJsonAsync("/api/auth/login", new { userName, password });
if (!login.IsSuccessStatusCode)
{
    Console.Error.WriteLine($"Could not sign in as {userName}: {login.StatusCode}");
    return 1;
}

var token = (await login.Content.ReadFromJsonAsync<JsonElement>())
    .GetProperty("accessToken").GetString();
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
Console.WriteLine($"    signed in as {userName}");

// --- Refuse to trample a real installation ---------------------------------
// A hospital's register is the one thing here that cannot be rebuilt, and the
// difference between a demo box and a live one is a port number in a URL.
var existing = await http.GetFromJsonAsync<JsonElement>("/api/equipment?pageSize=1");
var assetCount = existing.GetProperty("total").GetInt32();

if (assetCount > 5 && !force)
{
    Console.Error.WriteLine($"""

        This installation already has {assetCount} pieces of equipment, so it does not
        look like an empty demo box. Nothing has been changed.

        Pass --force if you are certain this is a throwaway installation.
        """);
    return 1;
}

var random = new Random(20260908);

// --- Locations -------------------------------------------------------------
// A real hospital's shape: an organisation, a site, buildings, floors,
// departments, and the rooms equipment actually stands in.
Console.WriteLine("==> Locations");

var org = await CreateLocationAsync("SAHYADRI", "Sahyadri Hospital, Pune", 10, null);
var site = await CreateLocationAsync("DECCAN", "Deccan Gymkhana", 20, org);

string[] buildings = ["Main Block", "Cardiac Wing", "Outpatient Block"];
string[] departments =
[
    "ICU", "NICU", "Cardiology", "Radiology", "Operation Theatre", "Emergency",
    "Dialysis", "Pathology Lab", "General Ward", "Private Ward", "CSSD",
    "Physiotherapy", "Labour Room", "Endoscopy", "Blood Bank", "Neurology",
];

var rooms = new List<int>();
var departmentIds = new List<int>();

for (var b = 0; b < buildings.Length; b++)
{
    var buildingId = await CreateLocationAsync($"BLD-{b + 1:00}", buildings[b], 30, site);

    for (var f = 0; f < 3; f++)
    {
        var floorId = await CreateLocationAsync(
            $"BLD-{b + 1:00}-F{f}", f == 0 ? "Ground Floor" : $"Floor {f}", 40, buildingId);

        for (var d = 0; d < 4; d++)
        {
            var name = departments[(b * 12 + f * 4 + d) % departments.Length];
            var deptId = await CreateLocationAsync(
                $"BLD-{b + 1:00}-F{f}-D{d + 1}", name, 50, floorId);
            departmentIds.Add(deptId);

            for (var r = 1; r <= random.Next(2, 5); r++)
            {
                rooms.Add(await CreateLocationAsync(
                    $"BLD-{b + 1:00}-F{f}-D{d + 1}-R{r:00}", $"{name} Room {r}", 60, deptId));
            }
        }
    }
}

Console.WriteLine($"    {rooms.Count} rooms across {departmentIds.Count} departments");

// --- Staff -----------------------------------------------------------------
// A biomedical department, not one login. Every role is represented because
// the point of the demo is that a head plans, engineers execute and
// technicians walk the rounds.
Console.WriteLine("==> Staff");

(string User, string Name, string Code, string Role)[] staff =
[
    ("s.deshmukh", "Dr S Deshmukh", "BME-01", "Admin"),
    ("a.kulkarni", "A Kulkarni", "BME-02", "Admin"),
    ("r.patil", "R Patil", "BME-03", "Admin"),
    ("m.shaikh", "M Shaikh", "BME-04", "Employee"),
    ("p.jadhav", "P Jadhav", "BME-05", "Employee"),
    ("v.more", "V More", "BME-06", "Employee"),
];

var staffIds = new List<int>();
foreach (var (user, name, code, role) in staff)
{
    var created = await http.PostAsJsonAsync("/api/users", new
    {
        userName = user,
        fullName = name,
        staffCode = code,
        role,
        password = "Hospital@2026",
    });

    if (created.IsSuccessStatusCode)
    {
        staffIds.Add((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32());
    }
}

Console.WriteLine($"    {staffIds.Count} staff, password Hospital@2026");

// --- Equipment -------------------------------------------------------------
Console.WriteLine("==> Equipment");

var types = await http.GetFromJsonAsync<JsonElement>("/api/lookups/equipment-types");
var typeByCode = types.EnumerateArray()
    .ToDictionary(t => t.GetProperty("code").GetString()!, t => t.GetProperty("id").GetInt32());

string[] manufacturers =
[
    "Philips", "GE Healthcare", "Siemens Healthineers", "Mindray", "Drager",
    "Nihon Kohden", "Skanray", "BPL Medical", "Schiller", "Fresenius",
];

// The types the demo's checklists cover get most of the fleet, so the PM
// programme has something to bite on. The rest is spread thinly to make the
// register look like a real hospital rather than ten of everything.
(string Code, int Count)[] fleet =
[
    ("ecg-machine", 22), ("defibrillator", 18), ("infusion-pump", 90),
    ("icu-ventilator", 26), ("multipara-monitor", 64), ("suction-machine", 40),
    ("steam-sterilizer", 8), ("pulse-oximeter", 55),
    ("syringe-pump", 45), ("nebulizer", 30), ("electric-hospital-bed", 60),
    ("wheelchair", 25), ("patient-trolley", 20), ("weighing-scale", 18),
    ("phototherapy-unit", 10), ("infant-radiant-warmer", 8),
    ("hemodialysis-machine", 12), ("centrifuge", 10), ("laboratory-microscope", 12),
    ("ultrasound-scanner", 6), ("mobile-xray", 4), 
];

var tag = 1;
var created_ = 0;

foreach (var (code, count) in fleet)
{
    if (!typeByCode.TryGetValue(code, out var typeId) || count == 0) continue;

    for (var i = 0; i < count; i++)
    {
        var purchased = new DateTime(2018, 1, 1).AddDays(random.Next(0, 2800));

        // Mostly working, a few in for repair, a couple condemned. A register
        // where everything is in service is a register nobody has kept.
        var status = random.Next(100) switch
        {
            < 88 => 20,   // In service
            < 94 => 30,   // Under repair
            < 98 => 10,   // In store
            _ => 40,      // Condemned
        };

        var response = await http.PostAsJsonAsync("/api/equipment", new
        {
            assetTag = $"BME-{tag:00000}",
            serialNumber = i % 5 == 0 ? null : $"{random.Next(100000, 999999)}-{(char)('A' + random.Next(26))}",
            equipmentTypeId = typeId,
            locationId = rooms[random.Next(rooms.Count)],
            manufacturer = manufacturers[random.Next(manufacturers.Length)],
            model = $"{(char)('A' + random.Next(26))}{random.Next(100, 999)}",
            status,
            purchaseDate = purchased.ToString("yyyy-MM-dd"),
            installationDate = purchased.AddDays(random.Next(5, 90)).ToString("yyyy-MM-dd"),
            warrantyExpiryDate = purchased.AddYears(random.Next(1, 6)).ToString("yyyy-MM-dd"),
            notes = i % 9 == 0 ? "Under AMC with the local vendor." : null,
        });

        tag++;
        if (response.IsSuccessStatusCode) created_++;
    }
}

Console.WriteLine($"    {created_} machines");

// --- Checklists ------------------------------------------------------------
Console.WriteLine("==> Checklists");

var scheduled = 0;
var publishedTemplates = new List<(int Id, Template Template)>();

foreach (var template in Checklists.All)
{
    if (!typeByCode.TryGetValue(template.TypeCode, out var typeId)) continue;

    var created = await http.PostAsJsonAsync("/api/checklists", new
    {
        equipmentTypeId = typeId,
        code = template.Code,
        name = template.Name,
        description = template.Description,
    });

    if (!created.IsSuccessStatusCode) continue;
    var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

    await http.PutAsJsonAsync($"/api/checklists/{id}/draft", new
    {
        definition = new
        {
            sections = template.Sections.Select(s => new
            {
                title = s.Title,
                items = s.Items.Select(i => new
                {
                    key = i.Key,
                    label = i.Label,
                    type = (int)i.Type,
                    required = i.Required,
                    guidance = i.Guidance,
                    unit = i.Unit,
                    min = i.Min,
                    max = i.Max,
                }),
            }),
        },
        changeNote = "First version",
    });

    var published = await http.PostAsJsonAsync($"/api/checklists/{id}/publish", new { });
    if (published.IsSuccessStatusCode)
    {
        publishedTemplates.Add((id, template));
    }
}

Console.WriteLine($"    {publishedTemplates.Count} published"
                  + $" ({Checklists.All.Count(t => t.Description == Checklists.FromManual || t.Description.StartsWith(Checklists.FromManual, StringComparison.Ordinal))} from a real manual)");

// --- PM programme ----------------------------------------------------------
// Anchored in the past so the demo opens on a department with history and a
// backlog, which is what any real hospital has. A programme that starts today
// shows an empty page and proves nothing.
Console.WriteLine("==> PM programme");

foreach (var (id, template) in publishedTemplates)
{
    var anchor = DateTime.Today.AddDays(-random.Next(300, 500));

    var response = await http.PostAsJsonAsync("/api/pm/schedules/bulk", new
    {
        checklistTemplateId = id,
        frequency = (int)template.Frequency,
        intervalDays = template.IntervalDays,
        anchorDate = anchor.ToString("yyyy-MM-dd"),
        graceDays = template.GraceDays,
        locationId = (int?)null,
        includeInStore = false,
    });

    if (response.IsSuccessStatusCode)
    {
        scheduled += (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("created").GetInt32();
    }
}

var tasks = await http.GetFromJsonAsync<JsonElement>("/api/pm/tasks?pageSize=1");
Console.WriteLine($"    {scheduled} schedules, {tasks.GetProperty("total").GetInt32()} open tasks");

// --- Completed PMs ---------------------------------------------------------
// The part that makes the demo worth looking at. A hospital that has never
// completed a PM has no compliance figure, no certificates and no history —
// which is exactly what an empty install already shows.
Console.WriteLine("==> Completing a realistic share of the work");

var completed = 0;
var outOfRange = 0;

for (var page = 1; page <= 12; page++)
{
    var batch = await http.GetFromJsonAsync<JsonElement>($"/api/pm/tasks?pageSize=50&page={page}");
    var items = batch.GetProperty("items").EnumerateArray().ToList();
    if (items.Count == 0) break;

    foreach (var task in items)
    {
        // Roughly three in four. A department at 100% is not credible and a
        // department at 20% is not a product demo.
        if (random.Next(100) >= 74) continue;

        var taskId = task.GetProperty("id").GetInt32();
        var form = await http.GetAsync($"/api/pm/tasks/{taskId}/form");
        if (!form.IsSuccessStatusCode) continue;

        var body = await form.Content.ReadFromJsonAsync<JsonElement>();
        var versionId = body.GetProperty("checklistTemplateVersionId").GetInt32();

        var answers = new Dictionary<string, object>();
        var thisOneStrays = random.Next(100) < 12;

        foreach (var section in body.GetProperty("definition").GetProperty("sections").EnumerateArray())
        {
            foreach (var item in section.GetProperty("items").EnumerateArray())
            {
                var key = item.GetProperty("key").GetString()!;
                var type = item.GetProperty("type").GetInt32();

                if (type == 30)
                {
                    var min = item.GetProperty("min").ValueKind == JsonValueKind.Number
                        ? item.GetProperty("min").GetDecimal() : 0m;
                    var max = item.GetProperty("max").ValueKind == JsonValueKind.Number
                        ? item.GetProperty("max").GetDecimal() : 100m;

                    // A reading outside its range is the finding a PM exists to
                    // produce, so the demo has to contain some.
                    var value = thisOneStrays && random.Next(100) < 35
                        ? max + (max - min) * 0.15m
                        : min + (max - min) * (decimal)random.NextDouble();

                    if (value > max) outOfRange++;
                    answers[key] = new { value = Math.Round(value, 2).ToString(System.Globalization.CultureInfo.InvariantCulture) };
                }
                else
                {
                    answers[key] = new { value = random.Next(100) < 4 ? "fail" : "pass" };
                }
            }
        }

        var done = await http.PostAsJsonAsync($"/api/pm/tasks/{taskId}/complete", new
        {
            checklistTemplateVersionId = versionId,
            answers,
            signedByName = staff[random.Next(3, staff.Length)].Name,
            performedAtUtc = DateTime.UtcNow.AddDays(-random.Next(0, 25)),
            clientSubmissionId = Guid.NewGuid(),
            notes = random.Next(100) < 15 ? "Cleaned and tested. No further action." : null,
        });

        if (done.IsSuccessStatusCode) completed++;
    }
}

Console.WriteLine($"    {completed} PMs recorded, {outOfRange} readings out of range");

// --- Work orders -----------------------------------------------------------
Console.WriteLine("==> Work orders");

(string Fault, int Priority)[] faults =
[
    ("Display intermittently blank", 20),
    ("Alarm sounding continuously with no cause", 30),
    ("Will not hold charge on battery", 20),
    ("Paper jams on every print", 10),
    ("Pump stops mid-infusion and alarms occlusion", 40),
    ("Physical damage to casing after a fall", 30),
    ("Fails self test on power up", 40),
    ("Reading drifts against the reference", 20),
];

var machines = await http.GetFromJsonAsync<JsonElement>("/api/equipment?pageSize=200&status=20");
var machineIds = machines.GetProperty("items").EnumerateArray()
    .Select(e => e.GetProperty("id").GetInt32()).ToList();

var orders = 0;
foreach (var i in Enumerable.Range(0, 34))
{
    var (fault, priority) = faults[random.Next(faults.Length)];

    var response = await http.PostAsJsonAsync("/api/work-orders", new
    {
        equipmentId = machineIds[random.Next(machineIds.Count)],
        faultDescription = fault,
        priority,
        outOfService = priority >= 30,
    });

    if (!response.IsSuccessStatusCode) continue;
    var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    orders++;

    // Spread across the lifecycle, so the board is not all one colour.
    var stage = random.Next(100);
    if (stage < 25) continue;

    if (staffIds.Count > 0)
    {
        await http.PostAsJsonAsync($"/api/work-orders/{id}/assign",
            new { assignedToUserId = staffIds[random.Next(staffIds.Count)] });
    }

    await http.PostAsJsonAsync($"/api/work-orders/{id}/notes",
        new { body = "Attended. Fault reproduced on the ward." });

    if (stage < 55) continue;

    await http.PostAsJsonAsync($"/api/work-orders/{id}/resolve", new
    {
        resolution = "Replaced the faulty part and tested. Returned to service.",
        backInService = true,
    });
}

Console.WriteLine($"    {orders} work orders across the lifecycle");

// --- A backup --------------------------------------------------------------
// So the Backups page is not the one screen in the demo that says "never".
Console.WriteLine("==> Backup");
var backup = await http.PostAsJsonAsync("/api/admin/backups/run", new { });
Console.WriteLine(backup.IsSuccessStatusCode ? "    taken" : "    skipped (not an installed system)");

Console.WriteLine();
Console.WriteLine("Done. Sign in as any of:");
foreach (var (user, name, _, role) in staff)
{
    Console.WriteLine($"    {user,-14} {name,-18} {role}   (Hospital@2026)");
}

return 0;

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

async Task<int> CreateLocationAsync(string code, string name, int level, int? parentId)
{
    var response = await http.PostAsJsonAsync("/api/locations", new { code, name, level, parentId });
    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
}
