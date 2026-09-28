using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ConnectedMining
{
    // The server reserves sections but never assumes it has their Unity scene objects loaded.
    // Only a current ZDO owner executes a command. A timeout is NOT retried: an absent
    // acknowledgement cannot establish that the previous owner did not already drop loot.
    internal sealed class Multiplayer
    {
        internal const int MaxSections = 4096;
        private const string Rpc = "tristan.connectedmining.protocol1";
        private const int Hello = 0, Settings = 1, Proposal = 2, Command = 3, Result = 4;
        private const int Done = 0, NotOwner = 1, Skipped = 2;
        private readonly Plugin plugin;
        private ZRoutedRpc registered;
        private ZNet session;
        private string epoch = "";
        private bool configured;
        private bool enabled;
        private float nextHello;
        private float lastSettings;
        private float nextWarning;
        private long nextTask;
        private ChainLedger ledger = new ChainLedger();
        private readonly Dictionary<long, float> peers = new Dictionary<long, float>();
        private readonly Dictionary<long, Job> jobs = new Dictionary<long, Job>();
        private readonly Dictionary<long, Task> tasks = new Dictionary<long, Task>();
        private readonly Queue<Task> waiting = new Queue<Task>();
        private readonly Queue<Incoming> incoming = new Queue<Incoming>();
        private readonly HashSet<long> executing = new HashSet<long>();
        private readonly Dictionary<long, Receipt> receipts = new Dictionary<long, Receipt>();
        private readonly List<PendingProposal> proposals = new List<PendingProposal>();
        private readonly List<Replacement> replacements = new List<Replacement>();

        internal float Tolerance { get; private set; }
        internal int PerFrame { get; private set; }
        internal string Prefixes { get; private set; }
        private float Now => Time.realtimeSinceStartup;
        private bool Server => ZNet.instance && ZNet.instance.IsServer();
        private long ServerId => Server ? ZNet.GetUID() : ZNet.instance?.GetServerPeer()?.m_uid ?? 0;
        internal bool Ready => ZNet.instance && registered == ZRoutedRpc.instance &&
            (Server ? plugin.LocalEnabled && AllPeersReady() : configured && enabled && Now - lastSettings < 12);

        private sealed class Ref
        {
            internal ZDOID Id;
            internal int Index, Kind;
            internal string Key => Id + ":" + Index;
            internal Ref(Deposit node) { Id = node.Id; Index = node.Index; Kind = node.Kind; }
            internal Ref(ZPackage p)
            {
                Id = p.ReadZDOID(); Index = p.ReadInt(); Kind = p.ReadInt();
                if (Id.IsNone() || Kind < 0 || Kind > 2 || Index < -1 || Index >= MaxSections || (Kind == 2 ? Index != -1 : Index < 0))
                    throw new InvalidOperationException("Invalid deposit reference.");
            }
            internal void Write(ZPackage p) { p.Write(Id); p.Write(Index); p.Write(Kind); }
            internal Deposit Resolve()
            {
                GameObject obj = ZNetScene.instance ? ZNetScene.instance.FindInstance(Id) : null;
                if (!obj) return null;
                Deposit node = Kind == 0 ? Deposit.FromArea(obj.GetComponent<MineRock5>(), Index) :
                    Kind == 1 ? Deposit.FromArea(obj.GetComponent<MineRock>(), Index) : null;
                if (Kind == 2)
                    foreach (Collider collider in obj.GetComponentsInChildren<Collider>())
                    {
                        node = Deposit.FromCollider(collider);
                        if (node != null && node.Id == Id && node.Kind == Kind) break;
                        node = null;
                    }
                if (node == null || !node.Collider || Deposit.FromCollider(node.Collider) == null) return null;
                node.Refresh();
                return node;
            }
        }
        private sealed class Job
        {
            internal long Id;
            internal HitData Hit;
            internal int Pending, Count;
            internal float Finished = -1;
        }
        private sealed class Task
        {
            internal long Id;
            internal Job Job;
            internal Ref Node;
            internal readonly DispatchLease Lease = new DispatchLease();
            internal float EarliestDispatch;
        }
        private sealed class Incoming
        {
            internal long Id;
            internal int Attempt;
            internal Ref Node;
            internal HitData Hit;
            internal double Deadline;
        }
        private sealed class Receipt
        {
            internal int Status;
            internal List<Ref> Children;
            internal float Time;
        }
        private sealed class PendingProposal
        {
            internal Plugin.Snapshot Snapshot;
            internal int Frame;
        }
        private sealed class Replacement
        {
            internal Incoming Request;
            internal List<Shape> Shapes;
            internal int Frame;
        }

        internal Multiplayer(Plugin plugin)
        {
            this.plugin = plugin;
            Tolerance = plugin.LocalTolerance;
            PerFrame = plugin.LocalPerFrame;
            Prefixes = plugin.LocalPrefixes;
        }

        internal void Reset()
        {
            jobs.Clear(); tasks.Clear(); waiting.Clear(); incoming.Clear(); executing.Clear();
            receipts.Clear(); proposals.Clear(); replacements.Clear(); peers.Clear();
            ledger = new ChainLedger(); nextTask = 0; configured = false; enabled = false;
            epoch = ""; nextHello = 0; nextWarning = 0; session = null;
        }

        internal void Tick()
        {
            if (!ZNet.instance || ZRoutedRpc.instance == null)
            {
                if (!ReferenceEquals(session, null)) Reset();
                return;
            }
            if (session != ZNet.instance)
            {
                Reset(); session = ZNet.instance;
                if (Server) epoch = Guid.NewGuid().ToString("N");
            }
            if (registered != ZRoutedRpc.instance)
            {
                registered = ZRoutedRpc.instance;
                registered.Register<ZPackage>(Rpc, Receive);
            }
            if (Server)
            {
                Tolerance = plugin.LocalTolerance; PerFrame = plugin.LocalPerFrame; Prefixes = plugin.LocalPrefixes;
            }
            if (Now >= nextHello)
            {
                nextHello = Now + 3;
                if (!Server && ServerId != 0) Send(ServerId, Packet(Hello));
                if (Server)
                    foreach (ZNetPeer peer in ZNet.instance.GetConnectedPeers())
                        if (Compatible(peer.m_uid)) SendSettings(peer.m_uid);
            }
            if (!Ready && Now >= nextWarning && (Server ? ZNet.instance.GetConnectedPeers().Count > 0 : ServerId != 0))
            {
                nextWarning = Now + 30;
                plugin.Warn("Connected mining is inactive: install Connected Mining " + Plugin.Version + " on the server and every connected player, and enable it in the server config.");
            }
            foreach (long id in receipts.Where(p => Now - p.Value.Time > 60).Select(p => p.Key).ToArray()) receipts.Remove(id);
            ProcessProposals();
            ProcessIncoming();
            if (Server) ProcessServer();
        }

        private bool Compatible(long peer) => peer == ZNet.GetUID() || peers.TryGetValue(peer, out float time) && Now - time < 12;
        private bool AllPeersReady() => ZNet.instance.GetConnectedPeers().All(p => Compatible(p.m_uid));
        private ZPackage Packet(int kind)
        {
            var p = new ZPackage(); p.Write(kind); p.Write(Plugin.Version); p.Write(epoch); return p;
        }
        private void Send(long peer, ZPackage p)
        {
            if (peer != 0) registered.InvokeRoutedRPC(peer, Rpc, p);
        }
        private void SendSettings(long peer)
        {
            var p = Packet(Settings); p.Write(plugin.LocalEnabled && AllPeersReady());
            p.Write(plugin.LocalTolerance); p.Write(plugin.LocalPerFrame); p.Write(plugin.LocalPrefixes);
            Send(peer, p);
        }
        private static List<Ref> ReadRefs(ZPackage p)
        {
            int count = p.ReadInt();
            if (count < 0 || count > MaxSections) throw new InvalidOperationException("Invalid section count.");
            var result = new List<Ref>(count);
            for (int i = 0; i < count; i++) result.Add(new Ref(p));
            return result;
        }
        private static void WriteRefs(ZPackage p, List<Ref> refs)
        {
            p.Write(refs.Count); foreach (Ref node in refs) node.Write(p);
        }
        private void Receive(long sender, ZPackage p)
        {
            try
            {
                if (!ZNet.instance || session != ZNet.instance) return;
                int kind = p.ReadInt(); string version = p.ReadString(); string incomingEpoch = p.ReadString();
                if (version != Plugin.Version) return;
                if (kind == Hello && Server)
                {
                    if (sender != ZNet.GetUID() && ZNet.instance.GetPeer(sender) == null) return;
                    peers[sender] = Now; SendSettings(sender); return;
                }
                if (kind == Settings && !Server && sender == ServerId)
                {
                    if (epoch != "" && epoch != incomingEpoch) Reset();
                    session = ZNet.instance; epoch = incomingEpoch;
                    enabled = p.ReadBool(); Tolerance = Mathf.Clamp(p.ReadSingle(), 0, 0.5f);
                    PerFrame = Mathf.Clamp(p.ReadInt(), 1, 64); Prefixes = p.ReadString();
                    configured = true; lastSettings = Now; return;
                }
                if (incomingEpoch != epoch || epoch == "") return;
                if (kind == Proposal && Server && Ready && Compatible(sender)) AcceptProposal(sender, p);
                else if (kind == Command && sender == ServerId) AcceptCommand(p);
                else if (kind == Result && Server) AcceptResult(sender, p);
            }
            catch (Exception e) { plugin.Warn("Rejected a Connected Mining message: " + e.Message); }
        }

        internal void Submit(Plugin.Snapshot snapshot)
        {
            // Delay every proposal equally to let replacement prefabs run Start; preserve
            // break order on a deposit owner even when one break transforms a prefab.
            proposals.Add(new PendingProposal { Snapshot = snapshot, Frame = Time.frameCount });
        }
        private void ProcessProposals()
        {
            foreach (PendingProposal pending in proposals.ToArray())
            {
                if (pending.Frame == Time.frameCount) continue;
                proposals.Remove(pending);
                if (!Ready) continue;
                try
                {
                    Plugin.Snapshot s = pending.Snapshot;
                    var nodes = s.Connected.Select(w => new Ref(w.Node)).ToList();
                    if (s.Source.Kind == 2)
                    {
                        Physics.SyncTransforms();
                        nodes.AddRange(plugin.Discover(s.SourceShapes, s.Source.Key, s.Hit).Select(w => new Ref(w.Node)));
                    }
                    nodes = nodes.GroupBy(n => n.Key).Select(g => g.First()).ToList();
                    if (nodes.Count >= MaxSections) { plugin.Warn("Chain exceeds the 4096-section network limit."); continue; }
                    var p = Packet(Proposal); new Ref(s.Source).Write(p);
                    HitData hit = s.Hit.Clone(); hit.Serialize(ref p); WriteRefs(p, nodes);
                    Send(ServerId, p);
                }
                catch (Exception e) { plugin.Warn("Could not propose a mining chain: " + e.Message); }
            }
        }
        private void AcceptProposal(long sender, ZPackage p)
        {
            var source = new Ref(p);
            var hit = new HitData(); hit.Deserialize(ref p);
            var nodes = ReadRefs(p).Where(n => n.Key != source.Key).GroupBy(n => n.Key).Select(g => g.First()).ToList();
            // Source may already be absent because the initiating hit destroyed it. If still
            // present, its confirmed owner must be the sender. Never require it loaded here.
            ZDO sourceZdo = ZDOMan.instance.GetZDO(source.Id);
            if (sourceZdo != null && sourceZdo.GetOwner() != sender) return;
            if (hit.m_attacker.IsNone() || hit.m_damage.m_pickaxe <= 0 || nodes.Count == 0) return;
            if (!ledger.TryStart(nodes.Select(n => n.Key).Concat(new[] { source.Key }), out long id)) return;
            var job = new Job { Id = id, Hit = hit, Count = nodes.Count + 1 };
            jobs.Add(id, job);
            foreach (Ref node in nodes) AddTask(job, node);
        }
        private void AddTask(Job job, Ref node)
        {
            var task = new Task { Id = ++nextTask, Job = job, Node = node };
            tasks.Add(task.Id, task); job.Pending++; waiting.Enqueue(task);
        }

        private void ProcessServer()
        {
            foreach (Task task in tasks.Values.ToArray())
                if (task.Lease.Expire(Now))
                {
                    plugin.Warn("Mining section timed out; it will not be retried because its previous owner may already have processed it.");
                    Finish(task);
                }
            foreach (Job job in jobs.Values.ToArray())
                if (job.Pending == 0 && job.Finished >= 0 && Now - job.Finished > 45)
                { ledger.Release(job.Id); jobs.Remove(job.Id); }
            int count = Math.Min(PerFrame, waiting.Count);
            for (int i = 0; i < count; i++)
            {
                if (tasks.Values.Count(t => t.Lease.InFlight) >= 64) break;
                Task task = waiting.Dequeue();
                if (!tasks.ContainsKey(task.Id)) continue;
                if (Now < task.EarliestDispatch) { waiting.Enqueue(task); continue; }
                if (!Ready) { Finish(task); continue; }
                ZDO zdo = ZDOMan.instance.GetZDO(task.Node.Id);
                long owner = zdo?.GetOwner() ?? 0;
                if (owner == 0 || !Compatible(owner)) { Finish(task); continue; }
                if (!task.Lease.Begin(owner, Now)) continue;
                var p = Packet(Command); p.Write(task.Id); p.Write(task.Lease.Attempt); task.Node.Write(p);
                p.Write(ZNet.instance.GetTimeSeconds() + 20);
                HitData hit = task.Job.Hit.Clone(); hit.Serialize(ref p);
                Send(owner, p);
            }
        }
        private void Finish(Task task)
        {
            if (!tasks.Remove(task.Id)) return;
            task.Job.Pending--;
            if (task.Job.Pending == 0) task.Job.Finished = Now;
        }
        private void AcceptCommand(ZPackage p)
        {
            long id = p.ReadLong();
            int attempt = p.ReadInt();
            if (receipts.TryGetValue(id, out Receipt cached)) { Reply(id, attempt, cached); return; }
            if (executing.Contains(id)) return;
            var request = new Incoming { Id = id, Attempt = attempt, Node = new Ref(p), Deadline = p.ReadDouble(), Hit = new HitData() };
            request.Hit.Deserialize(ref p);
            executing.Add(id); incoming.Enqueue(request);
        }
        private void ProcessIncoming()
        {
            foreach (Replacement replacement in replacements.ToArray())
            {
                if (replacement.Frame == Time.frameCount) continue;
                replacements.Remove(replacement);
                var children = new List<Ref>();
                try
                {
                    if (Ready && replacement.Request.Hit.GetAttacker() is Player)
                    {
                        Physics.SyncTransforms();
                        children = plugin.Discover(replacement.Shapes, replacement.Request.Node.Key, replacement.Request.Hit).Select(w => new Ref(w.Node)).ToList();
                    }
                }
                catch (Exception e) { plugin.Warn("Replacement discovery failed: " + e.Message); }
                Complete(replacement.Request, Done, children);
            }
            int count = Math.Min(PerFrame, incoming.Count);
            for (int i = 0; i < count; i++)
            {
                Incoming request = incoming.Dequeue();
                try
                {
                    if (!Ready || ZNet.instance.GetTimeSeconds() > request.Deadline) { Complete(request, Skipped); continue; }
                    ZDO zdo = ZDOMan.instance.GetZDO(request.Node.Id);
                    if (zdo == null) { Complete(request, Done); continue; }
                    if (!zdo.IsOwner()) { Complete(request, NotOwner); continue; }
                    Deposit node = request.Node.Resolve();
                    if (node == null || !node.Alive || !node.Allowed(request.Hit) || !(request.Hit.GetAttacker() is Player))
                    { Complete(request, Skipped); continue; }
                    var shapes = node.Kind == 2 ? Shape.Capture(node) : null;
                    plugin.Execute(node, request.Hit);
                    if (shapes != null && node.Health <= 0)
                        replacements.Add(new Replacement { Request = request, Shapes = shapes, Frame = Time.frameCount });
                    else Complete(request, node.Health <= 0 ? Done : Skipped);
                }
                catch (Exception e)
                {
                    plugin.Warn("Section execution stopped: " + e.Message);
                    Complete(request, Skipped);
                }
            }
        }
        private void Complete(Incoming request, int status, List<Ref> children = null)
        {
            executing.Remove(request.Id);
            var receipt = new Receipt { Status = status, Children = children ?? new List<Ref>(), Time = Now };
            // An explicit ownership refusal guarantees no damage was applied and can be
            // reconsidered after ownership stabilizes. All other outcomes are terminal.
            if (status != NotOwner) receipts[request.Id] = receipt;
            Reply(request.Id, request.Attempt, receipt);
        }
        private void Reply(long id, int attempt, Receipt receipt)
        {
            var p = Packet(Result); p.Write(id); p.Write(attempt); p.Write(receipt.Status); WriteRefs(p, receipt.Children);
            Send(ServerId, p);
        }
        private void AcceptResult(long sender, ZPackage p)
        {
            long id = p.ReadLong(); int attempt = p.ReadInt(); int status = p.ReadInt(); var children = ReadRefs(p);
            if (!tasks.TryGetValue(id, out Task task)) return;
            LeaseDecision decision = task.Lease.Reply(sender, attempt, status);
            if (decision == LeaseDecision.Ignore) return;
            if (decision == LeaseDecision.Retry)
            {
                task.EarliestDispatch = Now + 0.5f;
                waiting.Enqueue(task); return;
            }
            if (status == Done)
                foreach (Ref node in children)
                    if (task.Job.Count < MaxSections && ledger.TryExtend(task.Job.Id, node.Key))
                    { task.Job.Count++; AddTask(task.Job, node); }
            Finish(task);
        }
    }
}
