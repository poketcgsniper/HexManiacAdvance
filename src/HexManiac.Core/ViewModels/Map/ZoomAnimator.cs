using System;
using System.Diagnostics;

namespace HavenSoft.HexManiac.Core.ViewModels.Map {
   /// <summary>
   /// Tweens the map's zoom from the old scale to the new scale with an ease-out curve (it starts fast and settles gently).
   /// It only does the math. The caller asks for the scale once per frame and moves the maps itself.
   /// The clock is injectable so the animation can be tested without waiting.
   /// </summary>
   public class ZoomAnimator {
      /// <summary>A zoom takes about a quarter of a second.</summary>
      public const double DefaultDurationSeconds = .25;

      private static readonly Stopwatch DefaultStopwatch = Stopwatch.StartNew();

      /// <summary>The cadence the zoom aims for: one frame every 16.7 ms, the refresh of a 60 Hz display.</summary>
      public static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1.0 / 60);

      /// <summary>
      /// Frames closer together than this are skipped, so a display that refreshes faster than that (120, 144, 240 Hz) draws about 60 to 80 frames a second instead of 240.
      /// This is shorter than FrameInterval on purpose: a 60 Hz display never delivers its frames at exactly 16.7 ms (the render loop wobbles by a few milliseconds),
      /// and a frame that arrives 15 ms after the last one must still be drawn. If it were held back, the next one would come 32 ms after the last, and the zoom would run at 30 frames a second.
      /// </summary>
      public static readonly TimeSpan MinimumFrameInterval = TimeSpan.FromMilliseconds(12);

      /// <summary>
      /// If a frame takes longer than this to show up, redrawing the maps is more work than the machine can do smoothly (big maps, zoomed in, a slow display).
      /// Gliding would then only make every zoom step take longer than it did without the animation, so the zoom jumps to its end instead.
      /// (Only used when every map is moved one by one. When the view moves all the maps with one shared transform, see ZoomLayer, a frame costs the same for any number of maps.)
      /// </summary>
      public static readonly TimeSpan SlowFrameInterval = TimeSpan.FromMilliseconds(80);

      private readonly Func<TimeSpan> clock;
      private readonly TimeSpan duration;
      private double from, to = 1;
      private TimeSpan startTime, lastFrameTime;
      private bool isAnimating, hasDrawnFrame;

      /// <param name="clock">Says how much time has passed, from any starting point. The default is a real stopwatch.</param>
      /// <param name="durationSeconds">How long a zoom takes. Zero or less turns the animation off: every zoom jumps to its target.</param>
      public ZoomAnimator(Func<TimeSpan> clock = null, double durationSeconds = DefaultDurationSeconds) {
         this.clock = clock ?? (() => DefaultStopwatch.Elapsed);
         duration = TimeSpan.FromSeconds(Math.Max(0, durationSeconds));
      }

      /// <summary>True from Start until Update has returned the final scale.</summary>
      public bool IsAnimating => isAnimating;

      /// <summary>The scale the animation is heading to. When nothing is animating, the scale it stopped at.</summary>
      public double Target => to;

      /// <summary>The scale right now. Always the exact target once the time is up.</summary>
      public double Current => isAnimating ? ScaleAfter(clock() - startTime) : to;

      private double ScaleAfter(TimeSpan elapsed) {
         if (elapsed >= duration) return to;
         if (elapsed <= TimeSpan.Zero) return from;
         return from + (to - from) * EaseOut(elapsed.TotalSeconds / duration.TotalSeconds);
      }

      /// <summary>Begin zooming from one scale to another.</summary>
      public void Start(double fromScale, double toScale) {
         if (fromScale == toScale || duration <= TimeSpan.Zero) {
            Snap(toScale);
            return;
         }
         (from, to) = (fromScale, toScale);
         startTime = lastFrameTime = clock();
         hasDrawnFrame = false; // the first frame is due as soon as the display asks for one: the zoom starts moving right away
         isAnimating = true;
      }

      /// <summary>
      /// Head for a new scale starting from the scale that is on screen right now (not from where the last animation began or where it was heading),
      /// so another zoom in the middle of a zoom doesn't make the picture jump.
      /// </summary>
      public void Retarget(double toScale) => Start(Current, toScale);

      /// <summary>Stop animating and jump to a scale.</summary>
      public void Snap(double scale) {
         (from, to) = (scale, scale);
         isAnimating = false;
      }

      /// <summary>Call once per frame. Returns the scale to show. Once the time is up, this returns the exact target and IsAnimating becomes false.</summary>
      public double Update() {
         if (!isAnimating) return to;
         var now = clock(); // the clock is read once: the frame that finishes the animation must show the exact target
         var elapsed = now - startTime;
         lastFrameTime = now;
         hasDrawnFrame = true;
         if (elapsed >= duration) isAnimating = false;
         return ScaleAfter(elapsed);
      }

      /// <summary>
      /// True if the next frame should be drawn now: enough time has passed since the last one, or the animation is over and the exact target is due.
      /// The caller checks this every time the display refreshes, and calls Update only when it is true.
      /// Frames are paced by the clock and the scale of a frame is worked out from the clock too, so a frame that comes late (the machine was busy)
      /// never makes the zoom last longer: the next frame just shows where the zoom is by then.
      /// </summary>
      public bool IsFrameDue {
         get {
            if (!isAnimating) return false;
            var now = clock();
            if (now - startTime >= duration) return true;
            if (!hasDrawnFrame) return now > startTime;
            return now - lastFrameTime >= MinimumFrameInterval;
         }
      }

      /// <summary>
      /// True if the last frame was drawn so long ago that frames can't be drawn smoothly: the caller should stop animating and jump to the target.
      /// </summary>
      public bool IsFallingBehind => isAnimating && clock() - lastFrameTime > SlowFrameInterval;

      /// <summary>Maps the fraction of the time that has passed (0 to 1) to the fraction of the zoom that is done (0 to 1): fast at first, slow at the end.</summary>
      public static double EaseOut(double fraction) {
         if (fraction <= 0) return 0;
         if (fraction >= 1) return 1;
         var remaining = 1 - fraction;
         return 1 - remaining * remaining * remaining;
      }
   }
}
