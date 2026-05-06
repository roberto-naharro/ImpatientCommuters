using System.Collections.Generic;
using ColossalFramework;
using ICities;
using ImpatientCommuters.Util;

namespace ImpatientCommuters
{
    public class CommuterImpatienceLimiter : ThreadingExtensionBase
    {
        private const CitizenInstance.Flags WaitingFlags =
            CitizenInstance.Flags.OnPath | CitizenInstance.Flags.WaitingTransport;

        private const int StepMask = 0xF;
        private const int StepSize = CitizenManager.MAX_INSTANCE_COUNT / (StepMask + 1);
        private const float PMax = 0.08f;

        private CitizenInstance[] _instances;
        private Citizen[]         _citizens;
        private NetNode[]         _nodes;
        private NetSegment[]      _segments;
        private PathUnit[]        _pathUnits;
        private TransportLine[]   _lines;
        private Vehicle[]         _vehicles;

        private readonly ushort[] _passengerCount    = new ushort[NetManager.MAX_NODE_COUNT];
        private readonly ushort[] _capacityThreshold = new ushort[NetManager.MAX_NODE_COUNT];

        // Threshold cache is refreshed every N ticks so vehicle capacity changes
        // are reflected within ~2 seconds without walking every vehicle list every tick.
        private const int ThresholdRefreshTicks = 64;
        private int _thresholdAge;

        private bool _initialized;

        // ── Shared state read by Harmony patches ──────────────────────────────────

        // instanceId → TickCount when BoredOfWaiting was set by this mod.
        // Written on sim thread, read on main thread — access with BoredLock.
        internal static readonly object BoredLock = new object();
        internal static readonly Dictionary<uint, int> BoredAt = new Dictionary<uint, int>();

        // nodeId → list of TickCount values for each departure this mod triggered,
        // used to compute last-minute count and rolling average for stop info panel.
        internal static readonly object StopLock = new object();
        internal static readonly Dictionary<ushort, List<int>> StopDepartures =
            new Dictionary<ushort, List<int>>();

        // ─────────────────────────────────────────────────────────────────────────

        private void EnsureInitialized()
        {
            if (_initialized)
                return;
            _instances = CitizenManager.instance.m_instances.m_buffer;
            _citizens  = CitizenManager.instance.m_citizens.m_buffer;
            _nodes     = NetManager.instance.m_nodes.m_buffer;
            _segments  = NetManager.instance.m_segments.m_buffer;
            _pathUnits = PathManager.instance.m_pathUnits.m_buffer;
            _lines     = TransportManager.instance.m_lines.m_buffer;
            _vehicles  = VehicleManager.instance.m_vehicles.m_buffer;
            _initialized = true;
        }

        public override void OnBeforeSimulationTick()
        {
            if (!Settings.Instance.Enabled)
                return;

            EnsureInitialized();

            System.Array.Clear(_passengerCount, 0, _passengerCount.Length);
            if (++_thresholdAge >= ThresholdRefreshTicks)
            {
                System.Array.Clear(_capacityThreshold, 0, _capacityThreshold.Length);
                _thresholdAge = 0;
                CleanupSharedState();
            }

            for (int i = 0; i < _instances.Length; i++)
            {
                ref CitizenInstance inst = ref _instances[i];
                if (inst.m_path == 0 || (inst.m_flags & WaitingFlags) != WaitingFlags)
                    continue;

                ushort nodeId = GetStopNode(ref inst);
                if (nodeId == 0)
                    continue;

                _passengerCount[nodeId]++;

                if (_capacityThreshold[nodeId] == 0)
                    _capacityThreshold[nodeId] = ComputeThreshold(nodeId);
            }
        }

        public override void OnBeforeSimulationFrame()
        {
            if (!Settings.Instance.Enabled)
                return;

            EnsureInitialized();

            uint step       = SimulationManager.instance.m_currentFrameIndex & (uint)StepMask;
            uint startIndex = step * (uint)StepSize;
            uint endIndex   = startIndex + (uint)StepSize;

            for (uint i = startIndex; i < endIndex; i++)
            {
                ref CitizenInstance inst = ref _instances[i];
                if (inst.m_path == 0)
                    continue;
                if ((inst.m_flags & WaitingFlags) != WaitingFlags)
                    continue;
                if ((inst.m_flags & CitizenInstance.Flags.BoredOfWaiting) != 0)
                    continue;

                ushort nodeId = GetStopNode(ref inst);
                if (nodeId == 0)
                    continue;
                if (_passengerCount[nodeId] < _capacityThreshold[nodeId])
                    continue;

                float t          = inst.m_waitCounter / 255f;
                uint  citizenIdx = inst.m_citizen;
                float ageFactor  = citizenIdx != 0
                    ? Settings.GetAgeFactor(Citizen.GetAgeGroup(_citizens[citizenIdx].m_age))
                    : 1.0f;
                float destFactor = (Settings.Instance.DestinationFactorEnabled && citizenIdx != 0)
                    ? GetDestinationFactor(ref inst, ref _citizens[citizenIdx])
                    : 1.0f;

                float p    = PMax * t * t * ageFactor * destFactor;
                int   roll = (int)SimulationManager.instance.m_randomizer.Int32(1000u);
                if (roll >= (int)(p * 1000f))
                    continue;

                inst.m_flags      |= CitizenInstance.Flags.BoredOfWaiting;
                inst.m_waitCounter = byte.MaxValue;

                int now = System.Environment.TickCount;
                lock (BoredLock)
                    BoredAt[i] = now;

                lock (StopLock)
                {
                    List<int> times;
                    if (!StopDepartures.TryGetValue(nodeId, out times))
                    {
                        times = new List<int>();
                        StopDepartures[nodeId] = times;
                    }
                    times.Add(now);
                }

                Log.DebugLog("Citizen " + i + " left stop " + nodeId
                    + " (crowd=" + _passengerCount[nodeId]
                    + " threshold=" + _capacityThreshold[nodeId]
                    + " p=" + p.ToString("F3") + ")");
            }
        }

        private ushort GetStopNode(ref CitizenInstance inst)
        {
            uint pathId = inst.m_path;
            if (pathId == 0)
                return 0;
            PathUnit.Position pos = _pathUnits[pathId].GetPosition(inst.m_pathPositionIndex >> 1);
            return _segments[pos.m_segment].m_startNode;
        }

        private ushort ComputeThreshold(ushort nodeId)
        {
            ushort lineId = _nodes[nodeId].m_transportLine;
            if (lineId == 0)
                return ushort.MaxValue;

            ushort vehId = _lines[lineId].m_vehicles;
            int    total = 0;
            int    count = 0;
            int    limit = 0;
            while (vehId != 0 && ++limit < 512)
            {
                VehicleInfo info = _vehicles[vehId].Info;
                if (info != null)
                {
                    total += info.m_vehicleAI.GetPassengerCapacity(true);
                    count++;
                }
                vehId = _vehicles[vehId].m_nextLineVehicle;
            }

            if (count == 0)
                return ushort.MaxValue;

            float avg       = total / (float)count;
            float threshold = avg * Settings.Instance.ThresholdMultiplier;

            return threshold >= ushort.MaxValue
                ? ushort.MaxValue
                : (ushort)System.Math.Max(1, (int)threshold);
        }

        private static float GetDestinationFactor(ref CitizenInstance inst, ref Citizen citizen)
        {
            ushort target = inst.m_targetBuilding;
            if (target == 0)
                return 1.0f;

            if ((citizen.m_flags & Citizen.Flags.Tourist) != 0)
                return 1.3f;

            if (citizen.m_workBuilding != 0 && target == citizen.m_workBuilding)
            {
                return (citizen.m_flags & Citizen.Flags.Student) != 0
                    ? 0.5f
                    : 0.4f;
            }

            if (target == citizen.m_homeBuilding)
                return 0.7f;

            return 1.1f;
        }

        // Removes stale entries from shared dictionaries.
        // Called every ThresholdRefreshTicks ticks from the sim thread.
        private static void CleanupSharedState()
        {
            int now     = System.Environment.TickCount;
            int cutoff2 = 120000; // 2 minutes

            var toRemoveBored = new List<uint>();
            lock (BoredLock)
            {
                foreach (var kvp in BoredAt)
                    if (now - kvp.Value > cutoff2)
                        toRemoveBored.Add(kvp.Key);
                foreach (uint key in toRemoveBored)
                    BoredAt.Remove(key);
            }

            lock (StopLock)
            {
                foreach (var kvp in StopDepartures)
                {
                    var times = kvp.Value;
                    // Remove entries older than 2 minutes; list is appended chronologically.
                    int keep = 0;
                    while (keep < times.Count && now - times[keep] > cutoff2)
                        keep++;
                    if (keep > 0)
                        times.RemoveRange(0, keep);
                }
            }
        }
    }
}
