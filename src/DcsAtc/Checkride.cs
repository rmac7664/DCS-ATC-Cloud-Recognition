namespace DcsAtc;

/// FI4: checkride, the instructor speaks on the intercom: three patterns with touch and go, a straight-in approach (ILS if the runway has one, otherwise the
/// same 3° geometry) with go-around at 200 ft, a full-stop landing in the touchdown zone. The pilot works the radio himself.
/// Tolerances as in the FAA ACS: downwind altitude ±100 ft of pattern altitude, track ±10° from the reciprocal, airspeed ±10 kt around the leg's middle,
/// approach from 3 to 0.5 NM within 3/4 scale (localizer 1.9°, glide slope 0.5° around 3°), go-around not below 150 ft, touchdown in the touchdown zone
/// (first 900 m, at most a third of the runway). A deviation counts after 5 s (gusts); the instructor calls it once per leg.
class Checkride
{
    const double NM = 1852, Ft = 0.3048, Kt = 0.514444, Grace = 5;
    public readonly Airfield F;
    public string Rw;                  // runway in use (Program: the tower's)
    public int Task;                   // 0-2 patterns flown, 3 approach, 4 landing, 5 landed
    public string? Result;             // debrief line when finished
    public bool Cancelled;
    public bool Done => Result != null || Cancelled;
    readonly List<(string Say, string Text)> faults = new();
    readonly Dictionary<string, double> over = new();   // parameter -> out of tolerance since
    readonly HashSet<string> told = new();              // parameters already called on this leg
    bool air, leg, onFinal, minCalled;
    double spdMin = double.MaxValue, spdMax, minH = double.MaxValue, tdAt, lastX = double.NaN, lastZ;

    public Checkride(Airfield f, string rw) => (F, Rw) = (f, rw);

    public string Intro() => $"Checkride at {F.Name.Replace('-', ' ')}. Fly three patterns with touch and go, runway {Tower.RwSay(Rw)}. " +
                             $"Hold {F.PatternFt:0} feet on downwind, plus or minus one hundred, heading plus or minus ten degrees, airspeed plus or minus ten knots. You work the radio.";

    public List<string> Tick(Telemetry t, double now)
    {
        var say = new List<string>();
        if (Tower.Dist(t.X, t.Z, F.X, F.Z) > 15 * NM) { Cancelled = true; return new() { "Checkride cancelled, we are too far from the airfield." }; }
        var e = F.End(Rw);
        double a = -((t.X - e.ThrX) * e.Dx + (t.Z - e.ThrZ) * e.Dz), l = (t.X - e.ThrX) * e.Dz - (t.Z - e.ThrZ) * e.Dx;   // a: before the threshold (> 0), l: beside the centerline
        double hdg = t.Hdg * 180 / Math.PI, kt = t.Ias / Kt, h = (t.AltMsl - F.Elev) / Ft;
        double trk = double.IsNaN(lastX) || Tower.Dist(t.X, t.Z, lastX, lastZ) < 30 ? hdg : Math.Atan2(t.Z - lastZ, t.X - lastX) * 180 / Math.PI;   // ground track (crab angle in crosswind)
        (lastX, lastZ) = (t.X, t.Z);
        bool ground = Tower.OnGround(t);

        if (Task == 5)   // landed: result when slow on the runway
        {
            if (ground && kt < 40 || now - tdAt > 20) say.Add(Finish());
            return say;
        }
        if (!ground && t.Agl > 30) air = true;
        if (air && ground && F.OnRunway(t.X, t.Z, 45, 300))
        {
            air = false;
            Touchdown(t, hdg, now, say);
            return say;
        }
        if (!air) return say;

        // downwind: abeam the runway, 0.4-3 NM beside it, flying the reciprocal
        bool dw = Task < 3 && Math.Abs(l) is > 0.4 * NM and < 3 * NM && a < 1 * NM && a > -e.Len - 0.5 * NM && Tower.HdgDiff(trk, e.Hdg + 180) < 30;
        if (dw)
        {
            leg = true;
            double dev = h + F.Elev / Ft - F.PatternFt, td = Tower.HdgDiff(trk, e.Hdg + 180);
            Check("altitude", Math.Abs(dev) > 100, now, "Check altitude.", $"pattern {Num(Task + 1)} altitude",
                  L($"Platzrunde {Task + 1}: Höhe {dev:+0;-0} ft", $"pattern {Task + 1}: altitude {dev:+0;-0} ft"), say);
            Check("track", td > 10, now, "Check heading.", $"pattern {Num(Task + 1)} heading",
                  L($"Platzrunde {Task + 1}: Kurs {td:0}° daneben", $"pattern {Task + 1}: heading off by {td:0}°"), say);
            (spdMin, spdMax) = (Math.Min(spdMin, kt), Math.Max(spdMax, kt));
        }
        else if (leg)   // leg over: airspeed spread, fresh calls for the next leg
        {
            if (spdMax - spdMin > 20) faults.Add(($"pattern {Num(Task + 1)} airspeed", L($"Platzrunde {Task + 1}: Fahrt schwankte um {spdMax - spdMin:0} kt", $"pattern {Task + 1}: airspeed varied by {spdMax - spdMin:0} kt")));
            (leg, spdMin, spdMax) = (false, double.MaxValue, 0);
            over.Clear(); told.Clear();
        }

        // approach: final 0.3-4 NM, tolerances from 3 to 0.5 NM, minimums at 200 ft, go-around when climbing away
        if (Task == 3)
        {
            bool fin = a is > 0.3 * NM and < 4 * NM && Math.Abs(l) < NM && Tower.HdgDiff(hdg, e.Hdg) < 30;
            if (fin && !onFinal && a < 2.5 * NM && minH > 1e6)
                faults.Add(("final too short", L($"Anflug: erst {a / NM:0.0} NM vor der Schwelle auf dem Endanflug, verlangt 3 NM", $"approach: established only {a / NM:0.0} NM from the threshold, 3 NM required")));
            onFinal |= fin;
            if (fin && a is >= 0.5 * NM and <= 3 * NM)
            {
                double loc = Math.Atan2(l, a + e.Len + 300) * 180 / Math.PI, gs = Math.Atan2(t.AltMsl - F.Elev, a + 300) * 180 / Math.PI - 3;
                Check("loc", Math.Abs(loc) > 1.9, now, "Check localizer.", "localizer", L($"Anflug: Landekurs {loc:+0.0;-0.0}°", $"approach: localizer {loc:+0.0;-0.0}°"), say);
                Check("gs", Math.Abs(gs) > 0.5, now, "Check glide slope.", "glide slope", L($"Anflug: Gleitweg {gs:+0.0;-0.0}°", $"approach: glide slope {gs:+0.0;-0.0}°"), say);
            }
            if (onFinal && a < 1.5 * NM) minH = Math.Min(minH, h);
            if (onFinal && !minCalled && h <= 210 && a < 2 * NM) { minCalled = true; say.Add("Minimums."); }
            if (onFinal && minH < 1000 && (t.Vs > 2 && h > minH + 100 || a < -300))
            {
                if (minH < 150) faults.Add(("below minimums", L($"Durchstarten erst bei {minH:0} ft, Minimum 150 ft", $"go-around at {minH:0} ft, minimum 150 ft")));
                (Task, onFinal) = (4, false);
                over.Clear(); told.Clear();
                say.Add($"Go around. Last task: full stop landing runway {Tower.RwSay(Rw)} in the touchdown zone.");
            }
        }
        return say;
    }

    void Touchdown(Telemetry t, double hdg, double now, List<string> say)
    {
        var te = F.Ends.MinBy(x => Tower.HdgDiff(x.Hdg, hdg))!;
        double past = (t.X - te.ThrX) * te.Dx + (t.Z - te.ThrZ) * te.Dz, tdz = Math.Min(900, te.Len / 3);
        over.Clear(); told.Clear(); leg = false;
        if (Task < 3)
        {
            Task++;
            say.Add(Task < 3 ? $"Pattern {Num(Task)} complete."
                             : $"Pattern three complete. Next, {(F.IlsOn(Rw) ? "ILS" : "straight in")} approach runway {Tower.RwSay(Rw)} from at least three miles, go around at two hundred feet. Request a low approach from the tower.");
        }
        else if (Task == 3)
        {
            faults.Add(("landed instead of going around", L("Anflug: gelandet statt durchgestartet", "approach: landed instead of going around")));
            (Task, onFinal) = (4, false);
            say.Add($"That was a landing, not a go around. Take off again, last task: full stop landing runway {Tower.RwSay(Rw)} in the touchdown zone.");
        }
        else
        {
            if (past < 0) faults.Add(("touchdown short", L($"Aufsetzen {-past:0} m vor der Schwelle", $"touchdown {-past:0} m short of the threshold")));
            else if (past > tdz) faults.Add(("touchdown long", L($"Aufsetzen {past:0} m hinter der Schwelle, Aufsetzzone bis {tdz:0} m", $"touchdown {past:0} m past the threshold, touchdown zone ends at {tdz:0} m")));
            (Task, tdAt) = (5, now);
        }
    }

    /// Out of tolerance for Grace seconds: one call and one fault per leg.
    void Check(string key, bool bad, double now, string call, string say, string text, List<string> calls)
    {
        if (!bad) { over.Remove(key); return; }
        if (!over.TryGetValue(key, out var since)) over[key] = since = now;
        if (now - since < Grace || !told.Add(key)) return;
        calls.Add(call);
        faults.Add((say, text));
    }

    string Finish()
    {
        bool ok = faults.Count == 0;
        Result = L($"Prüfflug {F.Name}: ", $"Checkride {F.Name}: ") + (ok ? L("bestanden", "passed") : L("nicht bestanden – ", "failed – ") + string.Join(", ", faults.Select(f => f.Text)));
        return ok ? "Checkride complete. Passed, well done." : $"Checkride complete. Not passed: {string.Join(", ", faults.Select(f => f.Say).Distinct())}. Details in the debriefing.";
    }

    static string Num(int n) => Tower.Digits(n.ToString());

    /// Self-test: one ride inside the tolerances (passed), one with a high downwind, a low glide path and a long touchdown (failed with those reasons).
    internal static void SelfTest()
    {
        var kut = Airfield.Kutaisi();
        var e = kut.End("25");
        string Fly(double dwDev, double gsDev, double tdPast, List<string> calls)
        {
            var r = new Checkride(kut, "25");
            double now = 0;
            void At(double a, double l, double ftMsl, double hdg, double kt = 150, double vs = 0)
            {
                var (x, z) = e.At(a, l);
                calls.AddRange(r.Tick(new Telemetry(ftMsl * Ft, ftMsl * Ft - kut.Elev, kt * Kt, hdg * Math.PI / 180, x, z, 0, 0, 101325, vs), now++));
            }
            void Land(double past) { At(-past, 0, kut.Elev / Ft + 3, e.Hdg, 130); }
            At(-100, 0, kut.Elev / Ft + 3, e.Hdg, 0);   // on the runway, then takeoff
            for (int lap = 0; lap < 3; lap++)
            {
                At(-e.Len - 500, 0, kut.Elev / Ft + 500, e.Hdg);   // upwind
                for (double a = -e.Len; a <= 0.5 * NM; a += 150) At(a, -1.5 * NM, kut.PatternFt + (lap == 1 ? dwDev : 0), e.Hdg + 180);
                At(1.5 * NM, -0.8 * NM, kut.PatternFt - 400, e.Hdg + 270);   // base
                for (double a = 1 * NM; a > 0; a -= 150) At(a, 0, kut.Elev / Ft + Math.Tan(3 * Math.PI / 180) * (a + 300) / Ft, e.Hdg);
                Land(300);
            }
            for (double a = -e.Len; a <= 1 * NM; a += 300) At(a, -1.5 * NM, kut.PatternFt, e.Hdg + 180);   // downwind, extended to a 4 NM final
            for (double a = 4 * NM; a >= 750; a -= 100) At(a, 0, kut.Elev / Ft + (Math.Tan(3 * Math.PI / 180) * (a + 300) / Ft) * (a is < 2 * NM and > 1.3 * NM ? gsDev : 1), e.Hdg, 140, -3.5);
            At(0.5 * NM, 0, kut.Elev / Ft + 260, e.Hdg, 140, 8);   // go-around
            At(0.3 * NM, 0, kut.Elev / Ft + 400, e.Hdg, 150, 10);
            At(-e.Len - 500, 0, kut.Elev / Ft + 800, e.Hdg, 160, 10);
            for (double a = 2 * NM; a > 0; a -= 150) At(a, 0, kut.Elev / Ft + Math.Tan(3 * Math.PI / 180) * (a + 300) / Ft, e.Hdg, 140, -3.5);
            Land(tdPast);
            At(-tdPast - 800, 0, kut.Elev / Ft + 3, e.Hdg, 30);   // rollout
            return r.Result ?? "(kein Ergebnis)";
        }
        var good = new List<string>(); var bad = new List<string>();
        var rg = Fly(0, 1, 400, good);
        var rb = Fly(200, 0.6, 1300, bad);
        if (rg != L("Prüfflug Kutaisi: bestanden", "Checkride Kutaisi: passed") || good.Count(s => s.StartsWith("Pattern")) != 3 || !good.Contains("Minimums.") || !good.Any(s => s.StartsWith("Go around.")) ||
            good.Any(s => s.StartsWith("Check ")) || good[^1] != "Checkride complete. Passed, well done.")
            throw new Exception("Prüfflug innerhalb der Toleranz: " + rg + " || " + string.Join(" | ", good));
        if (!rb.StartsWith(L("Prüfflug Kutaisi: nicht bestanden – Platzrunde 2: Höhe +200 ft", "Checkride Kutaisi: failed – pattern 2: altitude +200 ft")) || !rb.Contains(L("Anflug: Gleitweg -", "approach: glide slope -")) ||
            !rb.Contains(L("Aufsetzen 1300 m hinter der Schwelle", "touchdown 1300 m past the threshold")) || rb.Contains(L("Kurs", "heading")) ||
            !bad.Contains("Check altitude.") || !bad.Contains("Check glide slope.") || !bad[^1].StartsWith("Checkride complete. Not passed: pattern two altitude, glide slope, touchdown long."))
            throw new Exception("Prüfflug außerhalb der Toleranz: " + rb + " || " + string.Join(" | ", bad));
        Console.WriteLine("OK   FI4 Prüfflug: innerhalb der Toleranz bestanden, Höhe/Gleitweg/Aufsetzpunkt daneben nicht bestanden");
    }
}
