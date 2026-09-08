namespace HospitalPm.DemoData;

/// <summary>
/// The checklists the demo ships with.
///
/// One of these is real. The ECG machine checklists are taken from the Mindray
/// BeneHeart R3/R3A Operator's Manual (PN 046-004651-00, issue 2020-02):
/// the intervals, the inspection points and the electrical-safety limits in
/// Appendix D are the manufacturer's own numbers, not mine.
///
/// Every other checklist here is DRAFTED from ordinary biomedical practice —
/// IEC 62353 electrical safety, typical manufacturer service intervals — and
/// says so in its own description. That distinction matters: a biomedical
/// engineer reading a wrong tolerance will lose confidence in everything else
/// on the screen, so the ones that are guesses admit it rather than borrowing
/// authority they have not earned.
///
/// Replace a drafted one the moment a real manual for that device is to hand.
/// </summary>
public static class Checklists
{
    public const string DraftWarning =
        "DRAFT — drawn from general biomedical practice, not from this device's "
        + "service manual. Review the intervals and tolerances against the "
        + "manufacturer's documentation before using it on real equipment.";

    public const string FromManual =
        "Taken from the Mindray BeneHeart R3/R3A Operator's Manual (2020-02), "
        + "sections 11.2 and Appendix D.";

    public static IReadOnlyList<Template> All =>
    [
        EcgDaily, EcgSixMonthly, EcgElectricalSafety,
        Defibrillator, InfusionPump, Ventilator, PatientMonitor,
        SuctionMachine, Autoclave, PulseOximeter,
    ];

    // ---------------------------------------------------------------- ECG --
    // Real. The manual specifies a visual inspection before first use each
    // day, a thorough inspection by qualified personnel every 6-12 months, a
    // battery performance test annually, and an electrical safety inspection
    // every two years.

    private static Template EcgDaily => new(
        TypeCode: "ecg-machine",
        Code: "pm-ecg-daily",
        Name: "ECG machine — daily user check",
        Description: FromManual + " Section 11.2: a visual inspection before the "
                     + "equipment's first use every day.",
        Frequency: Frequency.Monthly,
        GraceDays: 2,
        Sections:
        [
            new("Before first use", [
                Item("housing_intact", "Housing and display free from cracks and damage"),
                Item("keys_work", "All keys function properly"),
                Item("connectors_sound", "Connectors not loose, cracked or bent"),
                Item("cables_sound", "Cables free from cuts, nicks and fraying"),
                Item("cords_connected", "Power cord and patient cable securely connected"),
                Item("paper_loaded", "Recording paper properly loaded and sufficient"),
                Item("battery_charged", "Battery installed and has sufficient charge"),
                Item("electrodes_sound", "Chest electrode bulbs uncracked; limb electrodes clamp properly"),
            ]),
        ]);

    private static Template EcgSixMonthly => new(
        TypeCode: "ecg-machine",
        Code: "pm-ecg-6monthly",
        Name: "ECG machine — six-monthly service inspection",
        Description: FromManual + " Section 11.2 specifies a thorough inspection by "
                     + "qualified service personnel every 6 to 12 months, and 10.5.2 an "
                     + "annual battery performance test.",
        Frequency: Frequency.HalfYearly,
        GraceDays: 14,
        Sections:
        [
            new("Environment and supply", [
                Item("environment_ok", "Environment and power supply meet the stated requirements"),
                Item("no_mechanical_damage", "No mechanical damage to equipment or accessories"),
                Item("insulation_ok", "Power cord, patient cable and lead wires undamaged; insulation sound"),
                Item("specified_accessories", "Only specified accessories in use; electrode types and brands not mixed"),
            ]),
            new("Recorder", [
                Item("print_head_cleaned", "Thermal print head cleaned with 75% ethanol"),
                Item("printout_legible", "Printout legible and dark — light printing indicates a dirty head"),
                Item("recorder_works", "Recorder functions correctly and paper meets requirements"),
            ]),
            new("Battery", [
                // 10.5.2: charge fully, run on battery until shutdown, compare
                // against the A.3 specification of 2 hours continuous recording.
                Number("battery_runtime_h", "Measured run time on a full charge", "hours", 2.0m, 8.0m,
                       "Charge uninterrupted until the indicator goes out, then run on battery until "
                       + "shutdown. The manual specifies 2 hours of continuous recording."),
                Item("battery_age_ok", "Battery less than three years old and free from visual damage"),
            ]),
            new("Cleaning", [
                Item("cleaned", "Cleaned with an approved agent",
                     "Approved: diluted sodium hypochlorite, 3% hydrogen peroxide, 75% ethanol, "
                     + "70% isopropanol. Never immerse, never sterilise, no abrasives or acetone — "
                     + "unapproved substances void the warranty."),
            ]),
        ]);

    private static Template EcgElectricalSafety => new(
        TypeCode: "ecg-machine",
        Code: "pm-ecg-elec-safety",
        Name: "ECG machine — electrical safety inspection",
        Description: FromManual + " Appendix D. Performed every two years with a safety "
                     + "analyser by service personnel — section 11.6 states users cannot "
                     + "perform these tests themselves.",
        Frequency: Frequency.Custom,
        IntervalDays: 730,
        GraceDays: 30,
        Sections:
        [
            new("Power cord and plug (D.1)", [
                Item("plug_pins", "Plug pins unbroken, unbent and not discoloured"),
                Item("plug_body", "No physical damage to plug body or strain relief"),
                Item("cord_condition", "Cord undamaged and not deteriorated; no warmth in use"),
            ]),
            new("Enclosure (D.2, D.3)", [
                Item("enclosure_ok", "No physical damage to enclosure, meters, switches or connectors"),
                Item("no_fluid_residue", "No residue of fluid spillage"),
                Item("no_missing_parts", "No loose or missing knobs, dials or terminals"),
                Item("no_unusual_signs", "No unusual noises or burning smells, particularly from ventilation holes"),
                Item("labels_legible", "Manufacturer and warning labels present and legible"),
            ]),
            new("Measurements (D.4–D.8)", [
                Number("earth_resistance", "Protective earth resistance", "Ω", 0m, 0.2m,
                       "Tested at 25 A between the protective earth terminal and the power cord's "
                       + "earth pin. Limit 0.2 Ω, all countries."),
                Number("earth_leakage_nc", "Earth leakage, normal condition", "µA", 0m, 500m,
                       "IEC 60601-1 limit 500 µA. Run this test before any other leakage test."),
                Number("earth_leakage_sfc", "Earth leakage, single fault condition", "µA", 0m, 1000m,
                       "IEC 60601-1 limit 1000 µA, with open neutral."),
                Number("patient_leakage_nc", "Patient leakage, normal condition", "µA", 0m, 10m,
                       "CF applied part. Limit 10 µA."),
                Number("patient_leakage_sfc", "Patient leakage, single fault condition", "µA", 0m, 50m,
                       "CF applied part. Limit 50 µA."),
                Number("mains_on_applied", "Mains on applied part leakage", "µA", 0m, 50m,
                       "110% of mains applied to the applied part. CF limit 50 µA."),
                Number("patient_auxiliary", "Patient auxiliary current, normal condition", "µA", 0m, 10m,
                       "CF applied part. Limit 10 µA."),
            ]),
        ]);

    // ------------------------------------------------------- drafted ones --

    private static Template Defibrillator => new(
        TypeCode: "defibrillator",
        Code: "pm-defib-6monthly",
        Name: "Defibrillator — six-monthly PM",
        Description: DraftWarning,
        Frequency: Frequency.HalfYearly,
        GraceDays: 7,
        Sections:
        [
            new("Inspection", [
                Item("casing_intact", "Casing, screen and controls undamaged"),
                Item("paddles_clean", "Paddles or pads clean, undamaged and in date"),
                Item("cables_sound", "Patient cables and connectors sound"),
            ]),
            new("Function", [
                Item("self_test", "Power-on self test passes"),
                Number("energy_low", "Delivered energy at 50 J setting", "J", 42.5m, 57.5m,
                       "Measured into an analyser. Typically within ±15% of the selected energy."),
                Number("energy_high", "Delivered energy at 200 J setting", "J", 170m, 230m),
                Number("charge_time", "Charge time to maximum energy", "s", 0m, 10m),
                Item("ecg_trace", "ECG trace displays cleanly on all leads"),
                Item("printer_works", "Recorder prints legibly"),
            ]),
            new("Battery", [
                Number("battery_runtime_min", "Run time on battery under test load", "minutes", 90m, 600m),
                Item("battery_age", "Battery within its service life"),
            ]),
        ]);

    private static Template InfusionPump => new(
        TypeCode: "infusion-pump",
        Code: "pm-infusion-annual",
        Name: "Infusion pump — annual PM",
        Description: DraftWarning,
        Frequency: Frequency.Yearly,
        GraceDays: 14,
        Sections:
        [
            new("Inspection", [
                Item("casing_intact", "Casing and door mechanism undamaged"),
                Item("clamp_grips", "Tubing clamp grips correctly"),
                Item("pole_mount", "Pole clamp secure"),
            ]),
            new("Accuracy", [
                Number("rate_error_25", "Flow rate error at 25 mL/h", "%", -5m, 5m,
                       "Measured over at least 15 minutes with the manufacturer's specified set."),
                Number("rate_error_100", "Flow rate error at 100 mL/h", "%", -5m, 5m),
                Number("occlusion_pressure", "Occlusion alarm pressure", "mmHg", 300m, 800m),
                Item("air_alarm", "Air-in-line alarm triggers"),
                Item("door_alarm", "Door-open alarm triggers"),
                Item("occlusion_alarm", "Occlusion alarm triggers and stops infusion"),
            ]),
            new("Battery", [
                Number("battery_runtime_h", "Run time on battery at 25 mL/h", "hours", 4m, 24m),
            ]),
        ]);

    private static Template Ventilator => new(
        TypeCode: "icu-ventilator",
        Code: "pm-ventilator-6monthly",
        Name: "ICU ventilator — six-monthly PM",
        Description: DraftWarning,
        Frequency: Frequency.HalfYearly,
        GraceDays: 7,
        Sections:
        [
            new("Inspection", [
                Item("circuit_condition", "Breathing circuit and connectors undamaged"),
                Item("filters_changed", "Inspiratory and expiratory filters changed"),
                Item("humidifier_ok", "Humidifier and water trap sound"),
            ]),
            new("Function", [
                Item("self_test", "Power-on self test and circuit compliance test pass"),
                Item("leak_test", "Leak test passes"),
                Number("tidal_volume_error", "Delivered tidal volume error at 500 mL", "%", -10m, 10m),
                Number("peep_error", "PEEP error at 10 cmH2O", "cmH2O", -2m, 2m),
                Number("fio2_error", "Delivered FiO2 error at 50%", "%", -5m, 5m),
                Item("alarms_test", "High and low pressure alarms trigger"),
                Item("o2_failure_alarm", "Oxygen supply failure alarm triggers"),
            ]),
            new("Backup power", [
                Number("battery_runtime_min", "Run time on internal battery", "minutes", 30m, 240m),
            ]),
        ]);

    private static Template PatientMonitor => new(
        TypeCode: "multipara-monitor",
        Code: "pm-monitor-annual",
        Name: "Multipara monitor — annual PM",
        Description: DraftWarning,
        Frequency: Frequency.Yearly,
        GraceDays: 14,
        Sections:
        [
            new("Inspection", [
                Item("screen_ok", "Screen and touch response undamaged"),
                Item("leads_sound", "ECG leads, SpO2 probe and NIBP cuff and hose sound"),
            ]),
            new("Function", [
                Number("nibp_error", "NIBP error against reference at 120 mmHg", "mmHg", -3m, 3m),
                Number("spo2_error", "SpO2 error against simulator at 90%", "%", -3m, 3m),
                Number("hr_error", "Heart rate error at 60 bpm", "bpm", -2m, 2m),
                Item("alarms_audible", "Alarms audible at the nurses' station distance"),
                Item("trends_stored", "Trends and alarm history retained after power cycle"),
            ]),
        ]);

    private static Template SuctionMachine => new(
        TypeCode: "suction-machine",
        Code: "pm-suction-6monthly",
        Name: "Suction machine — six-monthly PM",
        Description: DraftWarning,
        Frequency: Frequency.HalfYearly,
        GraceDays: 7,
        Sections:
        [
            new("Inspection", [
                Item("jar_intact", "Collection jar and lid undamaged and sealing"),
                Item("tubing_ok", "Tubing free from cracks and blockage"),
                Item("filter_changed", "Bacterial filter changed"),
            ]),
            new("Function", [
                Number("max_vacuum", "Maximum vacuum achieved", "mmHg", 500m, 760m),
                Number("time_to_vacuum", "Time to reach 500 mmHg", "s", 0m, 20m),
                Item("regulator_works", "Vacuum regulator adjusts smoothly across its range"),
                Item("overflow_cutoff", "Overflow protection cuts off correctly"),
            ]),
        ]);

    private static Template Autoclave => new(
        TypeCode: "steam-sterilizer",
        Code: "pm-autoclave-quarterly",
        Name: "Steam sterilizer — quarterly PM",
        Description: DraftWarning,
        Frequency: Frequency.Quarterly,
        GraceDays: 7,
        Sections:
        [
            new("Inspection", [
                Item("door_seal", "Door gasket sound and sealing"),
                Item("chamber_clean", "Chamber and drain clean and free from scale"),
                Item("safety_valve", "Safety valve free and undamaged"),
            ]),
            new("Cycle verification", [
                Number("chamber_temp", "Chamber temperature at 121 °C setting", "°C", 121m, 124m),
                Number("chamber_pressure", "Chamber pressure at hold", "bar", 1.0m, 1.3m),
                Number("hold_time", "Sterilisation hold time", "minutes", 15m, 30m),
                Item("bowie_dick", "Bowie-Dick or equivalent test pack passes"),
                Item("biological_indicator", "Biological indicator result negative"),
                Item("chart_recorder", "Chart or printout records the cycle correctly"),
            ]),
        ]);

    private static Template PulseOximeter => new(
        TypeCode: "pulse-oximeter",
        Code: "pm-spo2-annual",
        Name: "Pulse oximeter — annual PM",
        Description: DraftWarning,
        Frequency: Frequency.Yearly,
        GraceDays: 14,
        Sections:
        [
            new("Inspection", [
                Item("probe_ok", "Probe and cable undamaged; emitter and detector clean"),
                Item("display_ok", "Display legible"),
            ]),
            new("Function", [
                Number("spo2_error_98", "SpO2 error against simulator at 98%", "%", -3m, 3m),
                Number("spo2_error_85", "SpO2 error against simulator at 85%", "%", -3m, 3m),
                Number("hr_error", "Pulse rate error at 60 bpm", "bpm", -3m, 3m),
                Item("low_sat_alarm", "Low saturation alarm triggers"),
                Number("battery_runtime_h", "Run time on battery", "hours", 4m, 40m),
            ]),
        ]);

    // ------------------------------------------------------------ helpers --

    private static ChecklistItem Item(string key, string label, string? guidance = null)
        => new(key, label, ItemType.PassFail, true, guidance, null, null, null);

    private static ChecklistItem Number(
        string key, string label, string unit, decimal min, decimal max, string? guidance = null)
        => new(key, label, ItemType.Number, true, guidance, unit, min, max);
}

public enum ItemType { PassFail = 10, YesNo = 20, Number = 30, Text = 40, Choice = 50 }

public enum Frequency { Monthly = 10, Quarterly = 20, HalfYearly = 30, Yearly = 40, Custom = 90 }

public sealed record ChecklistItem(
    string Key,
    string Label,
    ItemType Type,
    bool Required,
    string? Guidance,
    string? Unit,
    decimal? Min,
    decimal? Max);

public sealed record ChecklistSection(string Title, IReadOnlyList<ChecklistItem> Items);

public sealed record Template(
    string TypeCode,
    string Code,
    string Name,
    string Description,
    Frequency Frequency,
    int GraceDays,
    IReadOnlyList<ChecklistSection> Sections,
    int IntervalDays = 0);
