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

        // Stuck-request watchdog, in frames. On partial-WebGPU devices (e.g. blocklisted mobile
        // GPUs) a failed browser mapAsync can strand the request with NO completion callback at
        // all - hasError is never observed, single-flight pins the channel open, and the backend
        // keeps spamming the console. A healthy readback lands within a few frames, so a request
        // this old will never land. Counted in frames rather than wall-clock so an editor pause
        // with a request in flight cannot false-latch on resume.
        internal const int StuckRequestFrameLimit = 600;

        readonly System.Action _onGaveUp; // owner's one-shot reaction to the give-up latch (drop stale data, log)
        readonly System.Action<AsyncGPUReadbackRequest> _onCompleted; // cached: a per-request method group would allocate every frame
        System.Action<AsyncGPUReadbackRequest> _pendingOnLanded; // callback for the single in-flight request
        int _errorStreak; // consecutive errored requests; any VALID landing resets it
        int _requestIssuedFrame; // Time.frameCount when the in-flight request was issued

        /// <summary>True while a request is outstanding - at most one is ever in flight.</summary>
        internal bool InFlight { get; private set; }

        /// <summary>True on backends without AsyncGPUReadback (probed at construction), after
        /// MaxConsecutiveErrors consecutive failures, or after an in-flight request outlives
        /// StuckRequestFrameLimit. Owners serve queries from their analytic fallback in any of
        /// these cases; the latch is never cleared.</summary>
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
            LatchIfStuckInFlight();
            if (!CanRequest) return false;
            InFlight = true;
            _requestIssuedFrame = Time.frameCount;
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
            LatchIfStuckInFlight();
            if (!CanRequest) return false;
            if (source == null) throw new System.ArgumentNullException(nameof(source));
            InFlight = true;
            _requestIssuedFrame = Time.frameCount;
            _pendingOnLanded = onLanded;
            AsyncGPUReadback.Request(source, _onCompleted);
            return true;
        }

        // A request that outlived the watchdog will never land (see StuckRequestFrameLimit): give
        // up so owners fall back instead of the channel refusing new requests silently forever.
        // InFlight is deliberately left standing - there is no cancel API - and if the native
        // callback ever does run, OnCompleted drops the stale landing.
        void LatchIfStuckInFlight()
        {
            if (!InFlight || Unsupported) return;
            if (Time.frameCount - _requestIssuedFrame < StuckRequestFrameLimit) return;
            GiveUp();
        }

        void OnCompleted(AsyncGPUReadbackRequest req)
        {
            InFlight = false;
            System.Action<AsyncGPUReadbackRequest> onLanded = _pendingOnLanded;
            _pendingOnLanded = null;
            // Latched while this request was in flight (watchdog): the owner already switched to
            // its fallback - delivering a late landing now would hand it one stale refresh.
            if (Unsupported) return;
            if (!IsValidLanding(req))
            {
                if (++_errorStreak >= MaxConsecutiveErrors) GiveUp();
                return;
            }
            _errorStreak = 0;
            onLanded?.Invoke(req);
        }

        // hasError alone is NOT a reliable failure signal: on WebGPU a failed buffer mapAsync can
        // complete WITHOUT hasError and simply carry no data, which would reset the streak and
        // retry forever. A landing only counts when it actually delivered bytes. The try/catch is
        // this channel's graceful-degradation boundary, not error swallowing - a GetData throw on
        // a "completed" request is exactly the failure being detected.
        static bool IsValidLanding(AsyncGPUReadbackRequest req)
        {
            if (req.hasError) return false;
            try { return req.GetData<byte>().Length > 0; }
            catch (System.Exception) { return false; }
        }

        void GiveUp()
        {
            if (Unsupported) return;
            Unsupported = true;
            _onGaveUp?.Invoke();
        }
    }
}
