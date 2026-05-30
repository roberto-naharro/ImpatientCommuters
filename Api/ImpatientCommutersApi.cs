using System;
using System.Collections.Generic;

namespace ImpatientCommuters.Api
{
    // Public extension point for other mods (bound by reflection — no assembly reference
    // required either way). Any mod can register a predicate that exempts specific waiting
    // citizens from this mod's impatience / stop-abandonment behaviour.
    //
    // A predicate receives (citizenInstanceId, stopNodeId) and returns true to exempt that
    // citizen at that stop on this tick. It is evaluated on the simulation thread, once per
    // waiting citizen that would otherwise be a candidate to leave an overcrowded stop, so it
    // must be cheap and allocation-free.
    //
    // Example consumer (reflection):
    //   var api = Type.GetType("ImpatientCommuters.Api.ImpatientCommutersApi, ImpatientCommuters");
    //   var reg = api.GetMethod("RegisterExemption", new[]{ typeof(Func<ushort,ushort,bool>) });
    //   reg.Invoke(null, new object[]{ (Func<ushort,ushort,bool>)MyPredicate });
    //
    // KEEP THIS TYPE/METHOD SHAPE STABLE — it is a reflection contract.
    public static class ImpatientCommutersApi
    {
        public const int ApiVersion = 1;
        public static int GetApiVersion() => ApiVersion;

        private static readonly List<Func<ushort, ushort, bool>> _exemptions =
            new List<Func<ushort, ushort, bool>>();
        private static readonly object _sync = new object();

        // Lock-free snapshot for the hot read path; replaced wholesale on register/remove.
        private static volatile Func<ushort, ushort, bool>[] _snapshot =
            new Func<ushort, ushort, bool>[0];

        // Register a predicate. Idempotent: registering the same delegate twice is a no-op.
        public static void RegisterExemption(Func<ushort, ushort, bool> predicate)
        {
            if (predicate == null)
                return;
            lock (_sync)
            {
                if (!_exemptions.Contains(predicate))
                {
                    _exemptions.Add(predicate);
                    _snapshot = _exemptions.ToArray();
                }
            }
        }

        // Remove a previously-registered predicate (pass the same delegate / same method).
        public static void RemoveExemption(Func<ushort, ushort, bool> predicate)
        {
            if (predicate == null)
                return;
            lock (_sync)
            {
                if (_exemptions.Remove(predicate))
                    _snapshot = _exemptions.ToArray();
            }
        }

        // True if any registered predicate exempts this citizen. A misbehaving consumer must
        // never break the simulation, so each call is guarded.
        internal static bool IsExempt(ushort citizenInstanceId, ushort stopNodeId)
        {
            Func<ushort, ushort, bool>[] preds = _snapshot;
            if (preds.Length == 0)
                return false;
            for (int i = 0; i < preds.Length; i++)
            {
                try
                {
                    if (preds[i](citizenInstanceId, stopNodeId))
                        return true;
                }
                catch
                {
                    // ignore — one bad predicate shouldn't affect the rest or the sim
                }
            }
            return false;
        }

        internal static bool HasExemptions => _snapshot.Length > 0;
    }
}
