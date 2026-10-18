using static DcsAtc.Tower;

namespace DcsAtc;

/// FI6: CSAR for helicopter players, nothing is spawned. A pilot down on land (Flights: ejection) becomes a survivor at the ejection point, moved to where
/// the chute came down (X;chute, S_EVENT_LANDING_AFTER_EJECTION). The AWACS calls the helicopters on Guard; "will pick up" takes the nearest one, the survivor
/// talks on Guard (hears the helicopter within 3 NM, smoke by day, flare at night on request), landing within 50 m for 30 s = on board, landing again at least
/// 3 NM away (base, FARP, ship) = rescued. In the water no pickup (no hoist).
static class Csar
{
    const double NM = 1852, Kt = 0.514444, Ft = 0.3048;
    public class Survivor
    {
        public string Unit = "", Cs = "", Bulls = "";
        public int Side;
        public double X, Z, At, Px, Pz, GroundAt = -1, SignalAt = -1e9;
        public string? By;                 // unit of the helicopter that took him
        public bool Landed, Called, Heard, Aboard, Rescued, Near;
        public bool Active(double now) => Landed || now - At >= 90;   // chute down (event) or 90 s after the ejection
    }
    public static readonly List<Survivor> All = new();

    public static void Down(string unit, string cs, int side, double x, double z, double now, string bulls, bool water)
    {
        All.RemoveAll(s => s.Unit == unit && !s.Aboard);
        if (!water) All.Add(new() { Unit = unit, Cs = cs, Side = side, X = x, Z = z, At = now, Bulls = bulls });
    }
    public static void Dead(string unit) => All.RemoveAll(s => s.Unit == unit && !s.Aboard);
    /// Chute landed: the nearest survivor still waiting within 15 km.
    public static void Chute(double x, double z)
    {
        if (All.Where(v => !v.Landed && !v.Aboard).MinBy(v => Dist(v.X, v.Z, x, z)) is { } s && Dist(s.X, s.Z, x, z) < 15000) (s.X, s.Z, s.Landed) = (x, z, true);
    }
    public static Survivor? Of(string heli) => All.FirstOrDefault(s => s.By == heli);
    public static bool Free(int side, double now) => All.Any(s => s.Side == side && s.By == null && s.Active(now));

    /// "will pick up": the nearest free survivor of the side within 100 NM.
    public static Survivor? Take(string heli, int side, double x, double z, double now)
    {
        if (Of(heli) is { } mine) return mine;
        var s = All.Where(v => v.Side == side && v.By == null && v.Active(now) && Dist(v.X, v.Z, x, z) < 100 * NM).MinBy(v => Dist(v.X, v.Z, x, z));
        if (s != null) s.By = heli;
        return s;
    }

    /// Helicopters gone (destroyed, slot left): survivor free again, one on board is lost with it; old ones (3 h) are dropped.
    public static void Release(ICollection<string> helis, double now)
    {
        All.RemoveAll(s => s.Aboard && !helis.Contains(s.By!) || now - s.At > 3 * 3600);
        foreach (var s in All.Where(s => s.By != null && !helis.Contains(s.By))) (s.By, s.Heard, s.GroundAt) = (null, false, -1);
    }

    /// Survivors a helicopter of the side should be told about: AWACS call once each (Program, with an AWACS of the side).
    public static IEnumerable<Survivor> ToCall(int side, double now) => All.Where(s => s.Side == side && !s.Called && s.By == null && s.Active(now));

    static string Dir(double brg) => new[] { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" }[(int)Math.Round(brg / 45) % 8];
    /// "bearing two seven zero, 12 miles" from (x, z) to the survivor.
    public static string Bra(Survivor s, double x, double z) => $"bearing {Ops.Brg(Bearing(x, z, s.X, s.Z))}, {MilesTxt(Dist(x, z, s.X, s.Z))}";

    /// Per second for the helicopter that took him: lines ("S" survivor on Guard, "C" crew chief on the intercom); Rescued set at the end.
    public static List<(string Who, string Text)> Tick(Survivor s, string heli, Telemetry t, double now)
    {
        var say = new List<(string, string)>();
        double d = Dist(t.X, t.Z, s.X, s.Z);
        bool parked = OnGround(t) && t.Ias < 3 * Kt;
        if (!s.Aboard)
        {
            if (!s.Heard && d < 3 * NM && t.Agl < 3000 * Ft) { s.Heard = true; say.Add(("S", $"{heli}, {s.Cs}, I hear you, you are {Dir(Bearing(s.X, s.Z, t.X, t.Z))} of me.")); }
            if (!parked || d > 300) { (s.GroundAt, s.Near) = (-1, false); return say; }
            if (d > 50)
            {
                if (!s.Near) { s.Near = true; say.Add(("C", $"Survivor is {Math.Round(d / 10) * 10:0} meters {Dir(Bearing(t.X, t.Z, s.X, s.Z))}, too far to walk. Set down closer.")); }
                return say;
            }
            if (s.GroundAt < 0) { s.GroundAt = now; say.Add(("C", "Survivor in sight, he's coming to us.")); }
            else if (now - s.GroundAt >= 30) { (s.Aboard, s.Px, s.Pz, s.GroundAt) = (true, t.X, t.Z, -1); say.Add(("C", "Survivor on board, door closed. Clear to lift.")); }
            return say;
        }
        if (!parked || Dist(t.X, t.Z, s.Px, s.Pz) < 3 * NM) { s.GroundAt = -1; return say; }
        if (s.GroundAt < 0) s.GroundAt = now;
        else if (now - s.GroundAt >= 10) { s.Rescued = true; All.Remove(s); say.Add(("C", "Survivor delivered.")); }
        return say;
    }

    /// Smoke by day, flare at night (Program: SIGNAL command to the mission) when the helicopter is within 5 NM, at most once a minute; null = negative.
    public static string? Signal(Survivor s, Telemetry t, double now, bool night)
    {
        if (Dist(t.X, t.Z, s.X, s.Z) > 5 * NM || now - s.SignalAt < 60) return null;
        s.SignalAt = now;
        return night ? "flare" : "smoke";
    }

    /// Self-test: ejection, chute lands elsewhere, take, heard, too far, on board after 30 s, lift-off before resets, delivery 3 NM away; release when the helicopter is gone.
    internal static void SelfTest()
    {
        All.Clear();
        Down("U1", "Enfield one two", 2, 10000, 20000, 0, "bullseye two seven zero, 25 miles", false);
        Down("U2", "Colt one one", 2, 0, 0, 0, "", true);   // water: none
        bool early = Free(2, 30);
        Chute(10800, 20300);
        var s = Take("H1", 2, 10800 + 10 * NM, 20300, 40);
        if (early || s == null || s.X != 10800 || !s.Active(40) || Take("H2", 2, 0, 0, 40) != null || All.Count != 1 || Free(2, 40)) throw new Exception("CSAR: Ausstieg/Fallschirm/Übernahme");
        Telemetry At(double dx, double dz, double agl, double kt) => new(agl, agl, kt * Kt, 0, s.X + dx, s.Z + dz, 0, 0, 101325);
        var log = new List<string>();
        void T(Telemetry t, double now) => log.AddRange(Tick(s, "Pedro one one", t, now).Select(x => x.Who + ":" + x.Text));
        T(At(5 * NM, 0, 300, 100), 50);
        T(At(2 * NM, 0, 300, 100), 60);                     // heard: helicopter north of him
        T(At(200, 0, 1, 0), 70);                           // too far
        T(At(30, 0, 1, 0), 80); T(At(30, 0, 1, 0), 100);   // 20 s
        T(At(30, 0, 20, 30), 101);                         // lifted off: anew
        T(At(30, 0, 1, 0), 102); T(At(30, 0, 1, 0), 132);  // 30 s -> on board
        var smokeFar = Signal(s, At(6 * NM, 0, 300, 100), 133, false);
        T(At(2 * NM, 0, 1, 0), 200); T(At(2 * NM, 0, 1, 0), 215);   // too close to the pickup: not delivered
        T(At(4 * NM, 0, 1, 0), 300); T(At(4 * NM, 0, 1, 0), 311);   // delivered
        var all = string.Join(" | ", log);
        if (all != "S:Pedro one one, Enfield one two, I hear you, you are north of me. | C:Survivor is 200 meters south, too far to walk. Set down closer. | C:Survivor in sight, he's coming to us. | " +
                   "C:Survivor in sight, he's coming to us. | C:Survivor on board, door closed. Clear to lift. | C:Survivor delivered." || !s.Rescued || All.Count != 0 || smokeFar != null)
            throw new Exception("CSAR Ablauf: " + all);
        Down("U3", "Ford one one", 2, 0, 0, 0, "", false);
        var s3 = Take("H1", 2, 1000, 0, 100)!;
        Telemetry over3 = new(300, 300, 50, 0, s3.X, s3.Z, 0, 0, 101325);
        var sig = (Signal(s3, over3, 101, true), Signal(s3, over3, 130, true));
        Release(new List<string>(), 140);
        if (s3.By != null || sig != ("flare", null) || !Free(2, 140) || ToCall(2, 140).Count() != 1) throw new Exception($"CSAR: Signal/Freigabe {sig} {s3.By}");
        All.Clear();
        Console.WriteLine("OK   FI6 CSAR: Ausstieg, Fallschirmpunkt, Übernahme, gehört, zu weit, 30 s an Bord, Abheben setzt zurück, Übergabe ab 3 NM, Signal, Freigabe");
    }
}
