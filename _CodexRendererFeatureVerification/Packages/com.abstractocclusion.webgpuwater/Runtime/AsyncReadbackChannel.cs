// WebGpuWater - shared async GPU readback channel.
//
// WaterOceanFft and WaterSurfaceSampler carried byte-identical readback state machines: a single
// in-flight request flag, a cached completion delegate, and a consecutive-error streak that - at
// the same threshold in both - latches an "unsupported" fallback so a backend that persistently
// errors doesn't retry silently forever. One implementation here so the throttling and give-up
// semantics can never drift between owners. What to DO with landed data (buffer copy, region
// bookkeeping) stays with each owner via the per-request onLanded callback.
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class AsyncReadbackChannel
    {
        // Give up after this many consecutive errored requests and stay on the owner's fallback
        // path. ONE definition: this replaces WaterOceanFft.MaxReadbackErrors and
        // WaterSurfaceSampler.MaxConsecutiveReadbackErrors (both were 8).
        internal const int MaxConsecutiveErrors = 8;

        readonly System.Action _onGaveUp; // owner's one-shot reaction to the give-up latch (drop stale data, log)
        readonly System.Action<AsyncGPUReadbackRequest> _onCompleted; // cached: a per-request method group would allocate every frame
        System.Action<AsyncGPUReadbackRequest> _pendingOnLanded; // callback for the single in-flight request
        int _errorStreak; // consecutive errored requests; any success resets it

        /// <summary>True while a request is outstanding - at most one is ever in flight.</summary>
        internal bool InFlight { get; private set; }

        /// <summary>True on backends without AsyncGPUReadback (probed at construction) or after
        /// MaxConsecutiveErrors consecutive failures. Owners serve queries from their analytic
        /// fallback in either case; the latch is never cleared.</summary>
        internal bool Unsupported { get; private set; }

        /// <summary>True when Request would actually issue: nothing in flight, not given up.</summary>
        internal bool CanRequest => !InFlight && !Unsupported;

        internal AsyncReadbackChannel(System.Action onGaveUp = null)
        {
            _onGaveUp = onGaveUp;
            _onCompleted = OnCompleted;
            // Same ctor-time probe both owners performed before unification.
            Unsupported = !SystemInfo.supportsAsyncGPUReadback;
        }

        /// <summary>Issue a mip-0 readback unless one is already in flight or the channel has
        /// given up. onLanded runs only on SUCCESSFUL landings (errors are absorbed into the
        /// streak here); pass a cached delegate, as a method group allocates per call.
        /// Returns whether a request was actually issued.</summary>
        internal bool Request(RenderTexture source, TextureFormat format,
                              System.Action<AsyncGPUReadbackRequest> onLanded)
        {
            if (!CanRequest) return false;
            InFlight = true;
            _pendingOnLanded = onLanded;
            AsyncGPUReadback.Request(source, 0, format, _onCompleted);
            return true;
        }

        /// <summary>Issue a whole-buffer readback with the same single-flight and error-latch
        /// semantics as the texture overload. The owner is responsible for keeping the buffer alive
        /// until completion (WaterVolume teardown drains all GPU readbacks before disposing modules).</summary>
        internal bool Request(GraphicsBuffer source,
                              System.Action<AsyncGPUReadbackRequest> onLanded)
        {
            if (!CanRequest) return false;
            if (source == null) throw new System.ArgumentNullException(nameof(source));
            InFlight = true;
            _pendingOnLanded = onLanded;
            AsyncGPUReadback.Request(source, _onCompleted);
            return true;
        }

        void OnCompleted(AsyncGPUReadbackRequest req)
        {
            InFlight = false;
            System.Action<AsyncGPUReadbackRequest> onLanded = _pendingOnLanded;
            _pendingOnLanded = null;
            if (req.hasError)
            {
                if (++_errorStreak >= MaxConsecutiveErrors && !Unsupported)
                {
                    Unsupported = true;
                    _onGaveUp?.Invoke();
                }
                return;
            }
            _errorStreak = 0;
            onLanded?.Invoke(req);
        }
    }
}
