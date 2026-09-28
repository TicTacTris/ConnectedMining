using ConnectedMining;
using Mono.Cecil;
using Mono.Cecil.Cil;

int count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    count++;
    Console.WriteLine("PASS: " + name);
}

var ledger = new ChainLedger();
Check(ledger.TryStart(new[] { "deposit:0", "deposit:1", "deposit:1" }, out long first), "first miner reserves connected sections atomically");
Check(!ledger.TryStart(new[] { "deposit:1", "deposit:2" }, out _), "second miner cannot replace the first chain");
Check(ledger.TryStart(new[] { "deposit:2" }, out long separate), "rejected overlapping proposal leaves no partial claims");
Check(!ledger.TryExtend(first, "deposit:2"), "replacement expansion cannot steal a different chain's section");
Check(ledger.TryExtend(first, "replacement:0") && !ledger.TryExtend(first, "replacement:0"), "replacement sections are reserved once");
ledger.Release(first);
Check(ledger.TryStart(new[] { "deposit:0", "replacement:0" }, out _), "expired chain releases its own reservations");
Check(!ledger.TryStart(new[] { "deposit:2" }, out _), "releasing a chain preserves other chains");
Check(!new ChainLedger().TryExtend(first, "new-world:0"), "world reset does not accept old chain extensions");

var lease = new DispatchLease();
Check(lease.Begin(10, 0) && !lease.Begin(20, 0), "a section has only one in-flight owner");
Check(lease.Reply(20, 1, 0) == LeaseDecision.Ignore, "acknowledgement from wrong peer rejected");
Check(lease.Reply(10, 1, 1) == LeaseDecision.Retry && lease.Begin(20, 1), "explicit non-execution permits ownership reroute");
Check(lease.Reply(10, 1, 1) == LeaseDecision.Ignore, "delayed previous-owner refusal ignored");
Check(lease.Reply(20, 1, 0) == LeaseDecision.Ignore, "stale dispatch-attempt acknowledgement ignored");
Check(lease.Reply(20, 2, 0) == LeaseDecision.Finish && !lease.Begin(30, 2), "successful section is terminal");
Check(lease.Reply(20, 2, 0) == LeaseDecision.Ignore, "duplicate acknowledgement has no effect");
var timeoutLease = new DispatchLease();
timeoutLease.Begin(10, 5);
Check(!timeoutLease.Expire(26) && timeoutLease.Expire(28), "execution timeout uses dispatch age");
Check(!timeoutLease.Begin(20, 29) && timeoutLease.Reply(10, 1, 0) == LeaseDecision.Ignore, "ambiguous timeout is never replayed on a new owner");
var changing = new DispatchLease();
for (int i = 1; i <= 3; i++)
{
    Check(changing.Begin(10, i), "ownership retry starts attempt " + i);
    Check(changing.Reply(10, i, 1) == (i < 3 ? LeaseDecision.Retry : LeaseDecision.Finish), "ownership retries are bounded, attempt " + i);
}

Triangle[] Box(Vec min, Vec max)
{
    var points = Enumerable.Range(0, 8).Select(i => new Vec((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z)).ToArray();
    int[] indices = { 0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6, 0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5 };
    return Enumerable.Range(0, 12).Select(i => new Triangle(points[indices[i * 3]], points[indices[i * 3 + 1]], points[indices[i * 3 + 2]])).ToArray();
}
var origin = Box(new Vec(0, 0, 0), new Vec(1, 1, 1));
Check(Geometry.Touches(origin, Box(new Vec(1, 0, 0), new Vec(2, 1, 1)), 0), "face contact");
Check(Geometry.Touches(origin, Box(new Vec(1, 1, 1), new Vec(2, 2, 2)), 0), "corner contact");
Check(Geometry.Touches(origin, Box(new Vec(0.5, 0.5, 0.5), new Vec(2, 2, 2)), 0), "intersecting solids");
Check(Geometry.Touches(origin, Box(new Vec(0.2, 0.2, 0.2), new Vec(0.8, 0.8, 0.8)), 0), "fully enclosed solid");
Check(!Geometry.Touches(origin, Box(new Vec(1.11, 0, 0), new Vec(2, 1, 1)), 0.1), "gap larger than tolerance");
Check(Geometry.Touches(origin, Box(new Vec(1.09, 0, 0), new Vec(2, 1, 1)), 0.1), "gap inside tolerance");
Check(!Geometry.Touches(origin, Box(new Vec(1.08, 1.08, 0), new Vec(2, 2, 1)), 0.1), "diagonal gap uses Euclidean distance");
Triangle[] Tetra(Vec a, Vec b, Vec c, Vec d) => new[] { new Triangle(a, b, c), new Triangle(a, b, d), new Triangle(a, c, d), new Triangle(b, c, d) };
var low = Tetra(new Vec(0, 0, 0), new Vec(1, 0, 0), new Vec(0, 1, 0), new Vec(0, 0, 1));
var high = Tetra(new Vec(1, 1, 1), new Vec(0, 1, 1), new Vec(1, 0, 1), new Vec(1, 1, 0));
Check(!Geometry.Touches(low, high, 0.1), "identical bounding boxes do not connect separated solids");
var horizontal = new Triangle(new Vec(-1, 0, -1), new Vec(1, 0, -1), new Vec(0, 0, 1));
var crossing = new Triangle(new Vec(0, -1, 0), new Vec(0, 1, 0), new Vec(1, 1, 0));
Check(Geometry.DistanceSquared(horizontal, crossing) < 1e-10, "edge through face without shared vertices");
var away = new Triangle(new Vec(-1, 2, -1), new Vec(1, 2, -1), new Vec(0, 2, 1));
Check(Math.Abs(Geometry.DistanceSquared(horizontal, away) - 4) < 1e-10, "parallel triangles distance");
var point = new Triangle(new Vec(0, 2, 0), new Vec(0, 2, 0), new Vec(0, 2, 0));
Check(Math.Abs(Geometry.DistanceSquared(horizontal, point) - 4) < 1e-10, "degenerate triangle point");
var segment = new Triangle(new Vec(0, -1, 0), new Vec(0, 1, 0), new Vec(0, 1, 0));
Check(Geometry.DistanceSquared(horizontal, segment) < 1e-10, "capsule axis crosses mesh");
var shift = new Vec(5000, -120, -7000);
Triangle[] Shift(Triangle[] mesh) => mesh.Select(t => new Triangle(t.A + shift, t.B + shift, t.C + shift)).ToArray();
Check(!Geometry.Touches(Shift(origin), Shift(Box(new Vec(1.11, 0, 0), new Vec(2, 1, 1))), 0.1), "world-coordinate translation preserves separation");
var rng = new Random(401);
for (int i = 0; i < 300; i++)
{
    double x = rng.NextDouble() * 4 - 2, y = rng.NextDouble() * 4 - 2, z = rng.NextDouble() * 4 - 2;
    var other = Box(new Vec(x, y, z), new Vec(x + 0.7, y + 0.7, z + 0.7));
    double dx = Math.Max(0, Math.Max(x - 1, -x - 0.7));
    double dy = Math.Max(0, Math.Max(y - 1, -y - 0.7));
    double dz = Math.Max(0, Math.Max(z - 1, -z - 0.7));
    bool expected = dx * dx + dy * dy + dz * dz <= 0.01;
    if (Geometry.Touches(origin, other, 0.1) != expected || Geometry.Touches(other, origin, 0.1) != expected)
        throw new Exception($"Random solid distance regression at {x}, {y}, {z}");
}
Check(true, "300 randomized solid pairs match analytic box distance in both directions");

string gameDir = args.Length > 0 ? args[0] : @"D:\SteamLibrary\steamapps\common\Valheim";
string pluginPath = args.Length > 1 ? args[1] : @"D:\ValheimMods\ConnectedMining\bin\Release\net48\ConnectedMining.dll";
using var game = AssemblyDefinition.ReadAssembly(Path.Combine(gameDir, @"valheim_Data\Managed\assembly_valheim.dll"));
TypeDefinition Type(string name) => game.MainModule.Types.Single(t => t.Name == name);
void Method(string type, string name, string result, params string[] parameters)
{
    Check(Type(type).Methods.Any(m => m.Name == name && m.ReturnType.FullName == result && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters)), $"game hook {type}.{name}");
}
Method("MineRock5", "DamageArea", "System.Boolean", "System.Int32", "HitData");
Method("MineRock5", "LoadHealth", "System.Void");
Method("MineRock", "RPC_Hit", "System.Void", "System.Int64", "HitData", "System.Int32");
Method("Destructible", "RPC_Damage", "System.Void", "System.Int64", "HitData");
Check(Type("MineRock5").Fields.Any(f => f.Name == "m_hitAreas"), "segmented rock area collection");
var area = Type("MineRock5").NestedTypes.Single(t => t.Name == "HitArea");
Check(area.Fields.Any(f => f.Name == "m_health" && f.FieldType.FullName == "System.Single") && area.Fields.Any(f => f.Name == "m_collider" && f.FieldType.FullName == "UnityEngine.Collider"), "segment health and collider fields");
Check(Type("MineRock").Fields.Any(f => f.Name == "m_hitAreas" && f.FieldType.FullName == "UnityEngine.Collider[]"), "legacy rock area array");
Check(Type("Destructible").Fields.Any(f => f.Name == "m_destroyed" && f.FieldType.FullName == "System.Boolean"), "single-piece destruction state");
Method("ZNet", "GetTimeSeconds", "System.Double");
Method("ZNet", "GetServerPeer", "ZNetPeer");
Method("ZNet", "GetUID", "System.Int64");
Method("ZDO", "GetOwner", "System.Int64");
Method("ZDO", "IsOwner", "System.Boolean");
Method("ZRoutedRpc", "InvokeRoutedRPC", "System.Void", "System.Int64", "System.String", "System.Object[]");
using var plugin = AssemblyDefinition.ReadAssembly(pluginPath);
IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types) => types.SelectMany(t => new[] { t }.Concat(AllTypes(t.NestedTypes)));
var calls = AllTypes(plugin.MainModule.Types).SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Where(i => i.Operand is MethodReference).Select(i => (MethodReference)i.Operand).ToList();
var forbidden = new HashSet<string> { "TerrainComp", "TerrainModifier", "TerrainOp", "Heightmap", "Attack" };
Check(!calls.Any(m => forbidden.Contains(m.DeclaringType.Name)), "compiled plugin has no terrain deformation or attack calls");
Check(!calls.Any(m => m.Name is "UseStamina" or "DrainEquipedItemDurability" or "RaiseSkill"), "chain does not charge stamina/durability or grant weapon skill XP");
var allowedReferences = new HashSet<string> { "mscorlib", "System", "System.Core", "netstandard", "BepInEx", "0Harmony", "assembly_valheim", "assembly_utils", "UnityEngine", "UnityEngine.CoreModule", "UnityEngine.PhysicsModule", "UnityEngine.TerrainPhysicsModule" };
Check(plugin.MainModule.AssemblyReferences.All(r => allowedReferences.Contains(r.Name)), "only game/framework and BepInEx runtime dependencies");
Check(!calls.Any(m => m.Name is "ClaimOwnership" or "SetOwner"), "plugin never forcibly takes deposit ownership");
var readiness = plugin.MainModule.Types.Where(t => t.FullName is "ConnectedMining.Multiplayer" or "ConnectedMining.Plugin")
    .SelectMany(t => t.Methods).Where(m => m.Name is "get_Ready" or "get_CanRun").SelectMany(m => m.Body.Instructions);
Check(readiness.All(i => (i.Operand is not MethodReference m || m.Name is not "IsDedicated" and not "GetNrOfPlayers") &&
    (i.Operand is not FieldReference f || f.Name != "m_localPlayer")), "network readiness does not require a local player or reject dedicated servers");
Console.WriteLine($"All {count} checks passed.");
