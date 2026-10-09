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

      /// <summary>
      /// Frames closer together than this are skipped. The display may refresh 120 or 144 times a second, but the picture only has to move
      /// about 30 times a second: every frame re-lays-out and redraws every map on screen, so skipped frames are saved work.
      /// </summary>
      public static readonly TimeSpan MinimumFrameInterval = TimeSpan.FromMilliseconds(30);

      /// <summary>
      /// If a frame takes longer than this to show up, redrawing the maps is more work than the machine can do smoothly (big maps, zoomed in, a slow display).
      /// Gliding would then only make every zoom step take longer than it did without the animation, so the zoom jumps to its end instead.
      /// </summary>
      public static readonly TimeSpan SlowFrameInterval = TimeSpan.FromMilliseconds(80);

      private readonly Func<TimeSpan> clock;
      private readonly TimeSpan duration;
      private double from, to = 1;
      private TimeSpan startTime, lastFrameTime;
      private bool isAnimating;

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
         if (elapsed >= duration) isAnimating = false;
         return ScaleAfter(elapsed);
      }

      /// <summary>
      /// True if the next frame should be drawn now: enough time has passed since the last one, or the animation is over and the exact target is due.
      /// The caller checks this every time the display refreshes, and calls Update only when it is true.
      /// </summary>
      public bool IsFrameDue {
         get {
            if (!isAnimating) return false;
            var now = clock();
            return now - lastFrameTime >= MinimumFrameInterval || now - startTime >= duration;
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
