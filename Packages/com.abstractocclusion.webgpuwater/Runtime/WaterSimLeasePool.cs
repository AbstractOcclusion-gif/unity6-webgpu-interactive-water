// WebGpuWater - reusable pool of interactive-sim GPU contexts (Phase: simulation pooling v1).
//
// What this pools TODAY, honestly: WaterSimulation instances (the ping-pong heightfield / foam /
// flow RT set) across body enable/disable cycles - the streaming case, where zones spawn and
// despawn bodies and every cycle used to allocate and destroy a full RT set. A released context
// parks here (up to IdleRetentionCap per compute/resolution match) and the next body of the same
// shape reuses it after a full state clear, so no wake/foam ghosts cross bodies.
//
// What it does NOT do yet (see the 2026-08-29 research doc, section 3.5): live demand-leasing
// where a budget-paused body surrenders its RTs mid-session - today pausing stops dispatches but
// keeps memory (unchanged behaviour). The acquire/release seam here is the one that increment
// will reuse; ocean FFT cascade sharing is explicitly deferred.
//
// WebGPU-safe by construction: this file moves OWNERSHIP of existing resources; it adds no
// kernels, keywords or formats.
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Acquire/release seam for pooled WaterSimulation contexts, plus the debug view of
    /// who currently holds one and what sits idle.</summary>
    internal static class WaterSimLeasePool
    {
        // How many released contexts stay parked for reuse. Beyond it the oldest release is
        // disposed instead: idle VRAM is only worth holding while streaming is likely to want it
        // back, and the sim budget (WaterQuality tier) bounds how many can be active anyway.
        internal const int IdleRetentionCap = 4;

        internal readonly struct LeaseInfo
        {
            public readonly WaterSimulation Simulation;
            public readonly WaterVolume Owner;
            public readonly int Resolution;
            public readonly bool ReusedIdleContext;

            public LeaseInfo(WaterSimulation simulation, WaterVolume owner, int resolution,
                             bool reusedIdleContext)
            {
                Simulation = simulation;
                Owner = owner;
                Resolution = resolution;
                ReusedIdleContext = reusedIdleContext;
            }
        }

        struct IdleContext
        {
            public WaterSimulation Simulation;
            public ComputeShader Compute;
        }

        static readonly List<IdleContext> _idle = new List<IdleContext>();
        static readonly List<LeaseInfo> _leases = new List<LeaseInfo>();

        internal static int ActiveLeaseCount => _leases.Count;
        internal static int IdleContextCount => _idle.Count;
        internal static LeaseInfo GetLease(int index) => _leases[index];

        /// <summary>A simulation context for this body: a state-cleared idle match when one is
        /// parked, else a fresh allocation. Validation lives in WaterSimulation's constructor
        /// (fail fast, same messages as before pooling).</summary>
        internal static WaterSimulation Acquire(ComputeShader compute, int resolution,
                                                WaterVolume owner)
        {
            for (int i = 0; i < _idle.Count; i++)
            {
                IdleContext parked = _idle[i];
                if (parked.Compute != compute || parked.Simulation.Resolution != resolution)
                    continue;
                _idle.RemoveAt(i);
                // Clear BEFORE handing over, not at release: a disable-time clear would dispatch
                // into teardown, and a context idles cheaper dirty than cleared twice.
                parked.Simulation.ResetSimulationState();
                _leases.Add(new LeaseInfo(parked.Simulation, owner, resolution,
                                          reusedIdleContext: true));
                return parked.Simulation;
            }

            var created = new WaterSimulation(compute, resolution);
            _leases.Add(new LeaseInfo(created, owner, resolution, reusedIdleContext: false));
            return created;
        }

        /// <summary>Return a context. It parks for reuse while the idle store has room, else it
        /// is disposed - deterministically, oldest store wins, no per-frame RT churn either way.</summary>
        internal static void Release(WaterSimulation simulation, ComputeShader compute)
        {
            if (simulation == null) return;
            for (int i = 0; i < _leases.Count; i++)
            {
                if (_leases[i].Simulation != simulation) continue;
                _leases.RemoveAt(i);
                break;
            }

            if (compute == null || !ShouldRetainReleased(_idle.Count))
            {
                simulation.Dispose();
                return;
            }
            _idle.Add(new IdleContext { Simulation = simulation, Compute = compute });
        }

        // Pure retention policy, split out so tests pin the cap behaviour without GPU resources.
        internal static bool ShouldRetainReleased(int idleCount) => idleCount < IdleRetentionCap;

        // Cleared by WaterVolume.ResetStaticState for Fast Enter Play Mode (no domain reload).
        // Idle contexts are pool-owned and disposed here; leased ones belong to live modules,
        // whose own Dispose routes back through Release.
        internal static void ResetStaticState()
        {
            for (int i = 0; i < _idle.Count; i++) _idle[i].Simulation?.Dispose();
            _idle.Clear();
            _leases.Clear();
        }
    }
}
