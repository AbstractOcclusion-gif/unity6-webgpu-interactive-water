// WebGpuWater - the connected-water registry and the query handoff at authored seams.
//
// The graph half is deliberately thin: connections register like every other scene registry in
// this package (static list + ResetStaticState), and the game walks it through ConnectionsOf.
// The query half is the part gameplay feels: inside a connection's transition zone the resolved
// sample blends toward the OTHER side's surface, both sides converging to 50/50 at the seam
// plane, so a bobber drifting from river to lake never sees a height/velocity step.
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Registry and query surface for authored water connections.</summary>
    public static class WaterTopology
    {
        // The seam plane's maximum contribution from the other side. 0.5 makes the blend
        // symmetric: approaching the plane from either provider converges to the same mix.
        const float SeamPlaneBlend = 0.5f;

        static readonly List<WaterConnection> _connections = new List<WaterConnection>();
        static int _nextConnectionId;

        public static int ConnectionCount => _connections.Count;

        public static WaterConnection GetConnection(int index) => _connections[index];

        /// <summary>Fill results with the live connections touching bodyId; returns the count
        /// (capped by the array). Allocation-free so migration/streaming can poll it.</summary>
        public static int ConnectionsOf(int bodyId, WaterConnection[] results)
        {
            if (results == null) throw new System.ArgumentNullException(nameof(results));
            int written = 0;
            for (int i = 0; i < _connections.Count && written < results.Length; i++)
            {
                WaterConnection connection = _connections[i];
                if (!TouchesBody(connection, bodyId)) continue;
                results[written++] = connection;
            }
            return written;
        }

        internal static void Register(WaterConnection connection)
        {
            if (connection == null) throw new System.ArgumentNullException(nameof(connection));
            if (_connections.Contains(connection)) return;
            if (connection.ConnectionId == 0) connection.ConnectionId = ++_nextConnectionId;
            _connections.Add(connection);
        }

        internal static void Unregister(WaterConnection connection)
            => _connections.Remove(connection);

        // Cleared by WaterVolume.ResetStaticState for Fast Enter Play Mode (no domain reload).
        internal static void ResetStaticState()
        {
            _connections.Clear();
            _nextConnectionId = 0;
        }

        static bool TouchesBody(WaterConnection connection, int bodyId)
        {
            if (connection == null || !connection.IsWired) return false;
            IWaterSurfaceProvider a = connection.portA.ResolveProvider();
            IWaterSurfaceProvider b = connection.portB.ResolveProvider();
            return (a != null && a.BodyId == bodyId) || (b != null && b.BodyId == bodyId);
        }

        // The resolver calls this on every valid sample. Cost when no connection involves the
        // winner: one BodyId compare per live connection - connections are authored and few.
        internal static void ApplySeamBlend(IWaterSurfaceProvider winner, Vector3 point,
                                            in WaterDomainQueryOptions options,
                                            ref WaterDomainSample sample)
        {
            for (int i = 0; i < _connections.Count; i++)
            {
                WaterConnection connection = _connections[i];
                if (connection == null || !connection.IsWired) continue;

                IWaterSurfaceProvider a = connection.portA.ResolveProvider();
                IWaterSurfaceProvider b = connection.portB.ResolveProvider();
                if (a == null || b == null) continue;

                IWaterSurfaceProvider other;
                if (a.BodyId == winner.BodyId) other = b;
                else if (b.BodyId == winner.BodyId) other = a;
                else continue;

                // Distance to the seam PLANE (anchored at the port midpoint, normal along the
                // port axis): the transition zone is a slab, so drifting parallel to the seam
                // never changes the blend.
                Vector3 anchorA = connection.portA.Anchor;
                Vector3 anchorB = connection.portB.Anchor;
                Vector3 axis = anchorB - anchorA;
                float axisLength = axis.magnitude;
                if (axisLength <= Mathf.Epsilon) continue;
                Vector3 seamNormal = axis / axisLength;
                Vector3 seamCenter = (anchorA + anchorB) * 0.5f;
                float signedPlaneDistance = Vector3.Dot(point - seamCenter, seamNormal);
                float planeDistance = Mathf.Abs(signedPlaneDistance);
                if (planeDistance >= connection.TransitionRadiusMeters) continue;

                float blend = SeamPlaneBlend *
                              (1f - planeDistance / connection.TransitionRadiusMeters);
                // Sample the other side AT the seam plane, not at the query point: a bounded
                // provider cannot answer past its own footprint edge, and "the other side's
                // surface at the seam" is exactly the value the handoff must converge to.
                Vector3 seamPoint = point - signedPlaneDistance * seamNormal;
                if (!other.TrySampleSurface(seamPoint, options.Fields, options.MinimumWaveLength,
                                            options.ExcludeInteractiveRipples,
                                            out WaterSample otherSurface))
                    continue; // the other side cannot answer even at the seam (e.g. ribbon bank)

                sample.SurfaceHeight = Mathf.Lerp(sample.SurfaceHeight, otherSurface.Height, blend);
                if ((options.Fields & WaterQueryFields.Normal) != 0)
                    sample.SurfaceNormal =
                        Vector3.Slerp(sample.SurfaceNormal, otherSurface.Normal, blend).normalized;
                if ((options.Fields & WaterQueryFields.Velocity) != 0)
                    sample.Velocity = Vector3.Lerp(sample.Velocity, otherSurface.Velocity, blend);
                sample.SignedDepth = sample.SurfaceHeight - point.y;
                sample.Connection = new WaterConnectionInfo
                {
                    Active = true,
                    ConnectionId = connection.ConnectionId,
                    OtherBodyId = other.BodyId,
                    Blend = blend,
                };
                return; // one seam per sample: overlapping transition zones resolve to the first
                        // registered connection - deterministic, and stacked seams are an
                        // authoring smell worth surfacing rather than averaging away.
            }
        }
    }
}
