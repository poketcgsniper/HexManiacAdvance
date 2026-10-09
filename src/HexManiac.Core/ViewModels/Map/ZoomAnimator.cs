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

      private readonly Func<TimeSpan> clock;
      private readonly TimeSpan duration;
      private double from, to = 1;
      private TimeSpan startTime;
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
         startTime = clock();
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
         var elapsed = clock() - startTime; // the clock is read once: the frame that finishes the animation must show the exact target
         if (elapsed >= duration) isAnimating = false;
         return ScaleAfter(elapsed);
      }

      /// <summary>Maps the fraction of the time that has passed (0 to 1) to the fraction of the zoom that is done (0 to 1): fast at first, slow at the end.</summary>
      public static double EaseOut(double fraction) {
         if (fraction <= 0) return 0;
         if (fraction >= 1) return 1;
         var remaining = 1 - fraction;
         return 1 - remaining * remaining * remaining;
      }
   }
}
