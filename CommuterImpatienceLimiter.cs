using System.Collections.Generic;
using ColossalFramework;
using ICities;
using ImpatientCommuters.Util;
using UnityEngine;

namespace ImpatientCommuters
{
    public class CommuterImpatienceLimiter : ThreadingExtensionBase
    {
        private const CitizenInstance.Flags WaitingFlags =
            CitizenInstance.Flags.OnPath | CitizenInstance.Flags.WaitingTransport;

        private const int StepMask = 0xF;
        private const int StepSize = CitizenManager.MAX_INSTANCE_COUNT / (StepMask + 1);
        // Frustration: builds quadratically with wait time.
        private const float PMax = 0.08f;
        // Balking: spike on arrival at an overcrowded stop, decays as sunk cost builds.
        // Empirical basis: 50–70% of passengers skip excessively crowded vehicles on arrival
        // (queuing theory: balking). After committing to wait, abandonment rate drops due to
        // sunk-cost effect (Liu et al. 2022 survival analysis, transit queuing).
        private const float BalkMax   = 0.12f;
        private const float BalkDecay = 8f;   // exp(-BalkDecay * t): near-zero by t≈0.5

        private CitizenInstance[] _instances;
        private Citizen[]         _citizens;
        private NetNode[]         _nodes;
        private NetSegment[]      _segments;
        private PathUnit[]        _pathUnits;
        private TransportLine[]   _lines;
        private Vehicle[]         _vehicles;

        private readonly ushort[] _passengerCount     = new ushort[NetManager.MAX_NODE_COUNT];
        private readonly ushort[] _capacityThreshold  = new ushort[NetManager.MAX_NODE_COUNT];
        private readonly ushort[] _vehicleCount       = new ushort[NetManager.MAX_NODE_COUNT];
        private readonly ushort[] _stopCount          = new ushort[NetManager.MAX_NODE_COUNT];
        private readonly bool[]   _hasAlternativeLine = new bool[NetManager.MAX_NODE_COUNT];

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
                System.Array.Clear(_capacityThreshold,  0, _capacityThreshold.Length);
                System.Array.Clear(_vehicleCount,       0, _vehicleCount.Length);
                System.Array.Clear(_stopCount,          0, _stopCount.Length);
                System.Array.Clear(_hasAlternativeLine, 0, _hasAlternativeLine.Length);
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
                    ComputeNodeData(nodeId);
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

                // Extension point: any mod can register a predicate (see Api.ImpatientCommutersApi)
                // to exempt specific waiting citizens — e.g. School Buses keeps children waiting
                // for their assigned school bus. No-op when nothing is registered.
                if (Api.ImpatientCommutersApi.HasExemptions
                    && Api.ImpatientCommutersApi.IsExempt((ushort)i, nodeId))
                {
                    Log.DebugLog("Citizen " + i + " exempt from impatience at stop " + nodeId
                        + " (registered by another mod)");
                    continue;
                }

                float t          = inst.m_waitCounter / 255f;
                uint  citizenIdx = inst.m_citizen;

                Citizen.AgeGroup ageGroup = citizenIdx != 0
                    ? Citizen.GetAgeGroup(_citizens[citizenIdx].m_age)
                    : Citizen.AgeGroup.Adult;

                float ageFrustFactor = citizenIdx != 0 ? Settings.GetAgeFactor(ageGroup) : 1.0f;

                bool destEnabled = Settings.Instance.DestinationFactorEnabled && citizenIdx != 0;
                float destFrustFactor = destEnabled
                    ? GetDestinationFrustrationFactor(ref inst, ref _citizens[citizenIdx])
                    : 1.0f;

                // crowdRatio: 0 at threshold, 1 when double the threshold.
                float crowdRatio = System.Math.Min(
                    (_passengerCount[nodeId] - _capacityThreshold[nodeId]) / (float)_capacityThreshold[nodeId],
                    1f);

                float balk = 0f;
                if (Settings.Instance.BalkingEnabled)
                {
                    float ageBalkFactor  = citizenIdx != 0 ? Settings.GetAgeBalkFactor(ageGroup) : 1.0f;
                    float destBalkFactor = destEnabled
                        ? GetDestinationBalkFactor(ref inst, ref _citizens[citizenIdx])
                        : 1.0f;
                    balk = BalkMax * crowdRatio * (float)System.Math.Exp(-BalkDecay * t)
                           * ageBalkFactor * destBalkFactor;
                }

                float frustration = PMax * t * t * ageFrustFactor * destFrustFactor;

                // Frequency factor: headway ∝ stopCount / vehicleCount.
                // Baseline 5 stops/vehicle → neutral (×1.0). Metro with 6 stops / 4 trains ≈ ×0.55.
                // A 100-stop line with 1 bus gets ×3.0 (capped) — passengers should definitely leave.
                float freqFactor = Settings.Instance.FrequencyScalingEnabled
                    ? FrequencyFactor(_vehicleCount[nodeId], _stopCount[nodeId])
                    : 1.0f;

                // Alternative-line bonus: knowing another option is nearby raises willingness to leave.
                float multiLineFactor = (Settings.Instance.AlternativeLineBonusEnabled && _hasAlternativeLine[nodeId])
                    ? 1.15f
                    : 1.0f;
                float p           = (balk + frustration) * freqFactor * multiLineFactor;

                int roll = (int)SimulationManager.instance.m_randomizer.Int32(1000u);
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

        private void ComputeNodeData(ushort nodeId)
        {
            ushort lineId = _nodes[nodeId].m_transportLine;
            if (lineId == 0)
            {
                _capacityThreshold[nodeId] = ushort.MaxValue;
                return;
            }

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
            {
                _capacityThreshold[nodeId] = ushort.MaxValue;
                return;
            }

            float avg       = total / (float)count;
            float threshold = avg * Settings.Instance.ThresholdMultiplier;

            _capacityThreshold[nodeId] = threshold >= ushort.MaxValue
                ? ushort.MaxValue
                : (ushort)System.Math.Max(1, (int)threshold);

            _vehicleCount[nodeId] = (ushort)System.Math.Min(count, ushort.MaxValue);

            // Single pass: count stops on this line AND detect nearby alternative lines.
            // Combining both scans avoids iterating the node buffer twice per active stop.
            Vector3 pos = _nodes[nodeId].m_position;
            float sqrRadius = 150f * 150f;
            int   stops  = 0;
            bool  hasAlt = false;
            for (int i = 1; i < _nodes.Length; i++)
            {
                ref NetNode n = ref _nodes[i];
                if ((n.m_flags & NetNode.Flags.Created) == 0 || n.m_transportLine == 0)
                    continue;
                if (n.m_transportLine == lineId)
                {
                    stops++;
                }
                else if (!hasAlt)
                {
                    float dx = n.m_position.x - pos.x;
                    float dz = n.m_position.z - pos.z;
                    if (dx * dx + dz * dz < sqrRadius)
                        hasAlt = true;
                }
            }
            _stopCount[nodeId]          = (ushort)System.Math.Min(stops, ushort.MaxValue);
            _hasAlternativeLine[nodeId] = hasAlt;
        }

        // headway ∝ stopCount / vehicleCount. Baseline 5 stops/vehicle = neutral (×1.0).
        private static float FrequencyFactor(int vehicles, int stops)
        {
            if (vehicles <= 0) return 1.0f;
            double headwayProxy = System.Math.Max(stops, 1) / (double)vehicles;
            return (float)System.Math.Max(0.3, System.Math.Min(3.0, System.Math.Sqrt(headwayProxy / 5.0)));
        }

        // Destination factor for balking (immediate departure decision).
        // Work commuters respond strongly to crowding because they plan alternatives
        // in advance (Kim et al. 2009). Tourists don't know the network — low balk.
        private static float GetDestinationBalkFactor(ref CitizenInstance inst, ref Citizen citizen)
        {
            ushort target = inst.m_targetBuilding;
            if (target == 0)
                return 1.0f;

            if ((citizen.m_flags & Citizen.Flags.Tourist) != 0)
                return 0.7f;   // don't know alternatives

            if (citizen.m_workBuilding != 0 && target == citizen.m_workBuilding)
            {
                return (citizen.m_flags & Citizen.Flags.Student) != 0
                    ? 0.8f    // school — some obligation, knows some alternatives
                    : 1.2f;   // work — planned alternatives, crowding aversion
            }

            if (target == citizen.m_homeBuilding)
                return 0.9f;  // knows the route, mild crowding sensitivity

            return 1.0f;      // leisure — neutral
        }

        // Destination factor for frustration (accumulated wait abandonment).
        // Strong obligation (work, school) suppresses long-wait abandonment.
        private static float GetDestinationFrustrationFactor(ref CitizenInstance inst, ref Citizen citizen)
        {
            ushort target = inst.m_targetBuilding;
            if (target == 0)
                return 1.0f;

            if ((citizen.m_flags & Citizen.Flags.Tourist) != 0)
                return 1.3f;  // no schedule, high frustration once committed

            if (citizen.m_workBuilding != 0 && target == citizen.m_workBuilding)
            {
                return (citizen.m_flags & Citizen.Flags.Student) != 0
                    ? 0.5f    // school — obligation
                    : 0.4f;   // work — strong obligation
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
