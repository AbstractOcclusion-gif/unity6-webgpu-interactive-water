using System;
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {
        private readonly List<SimulationFocusRequest> simulationFocusRequests =
            new List<SimulationFocusRequest>();

        /// <summary>
        /// Temporarily center the interactive simulation on a world target.
        /// Highest priority wins, newest breaks ties. Dispose restores the previous request or
        /// authored focus/camera, without modifying serialized settings.
        /// Affects windowed bodies; ocean geometry follows the same resolved frame.
        /// Call and dispose on Unity's main thread.
        /// </summary>
        public IDisposable PushSimulationFocus(UnityEngine.Object owner, Transform target)
            => PushSimulationFocus(owner, target, null);

        public IDisposable PushSimulationFocus(UnityEngine.Object owner, Transform target,
            WaterSimulationWindowSettings settings, int priority = 0)
        {
            if (!owner) throw new ArgumentNullException(nameof(owner));
            if (!target) throw new ArgumentNullException(nameof(target));
            var request = new SimulationFocusRequest(this, owner, target, settings, priority);
            simulationFocusRequests.Add(request);
            return request;
        }

        internal void ResolveSimulationFocus(out Transform focus, out Vector2 offset)
            => ResolveSimulationFocus(out focus, out offset, out _);

        internal void ResolveSimulationFocus(out Transform focus, out Vector2 offset,
            out WaterSimulationWindowSettings settings)
        {
            SimulationFocusRequest best = null;
            for (int i = simulationFocusRequests.Count - 1; i >= 0; i--)
            {
                SimulationFocusRequest request = simulationFocusRequests[i];
                if (!request.Owner || !request.Target)
                {
                    simulationFocusRequests.RemoveAt(i);
                    continue;
                }
                if (best == null || request.Priority > best.Priority) best = request;
            }
            if (best != null)
            {
                focus = best.Target;
                offset = Vector2.zero;
                settings = best.Settings;
                return;
            }
            focus = simWindowFocus;
            offset = simWindowOffset;
            settings = null;
        }

        private sealed class SimulationFocusRequest : IDisposable
        {
            private WaterVolume body;
            internal readonly UnityEngine.Object Owner;
            internal readonly Transform Target;
            internal readonly WaterSimulationWindowSettings Settings;
            internal readonly int Priority;

            internal SimulationFocusRequest(WaterVolume body,
                UnityEngine.Object owner, Transform target,
                WaterSimulationWindowSettings settings, int priority)
            {
                this.body = body;
                Owner = owner;
                Target = target;
                Settings = settings;
                Priority = priority;
            }

            public void Dispose()
            {
                if (body != null) body.simulationFocusRequests.Remove(this);
                body = null;
            }
        }
    }
}
