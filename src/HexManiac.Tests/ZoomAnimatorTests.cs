using HavenSoft.HexManiac.Core.ViewModels.Map;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   /// <summary>The map editor eases from one zoom level to the next. These tests use a clock they move by hand, so none of them waits.</summary>
   public class ZoomAnimatorTests {
      private TimeSpan now = TimeSpan.FromSeconds(100);

      private ZoomAnimator Create(double seconds = 0.25) => new ZoomAnimator(() => now, seconds);

      private void Wait(double seconds) => now += TimeSpan.FromSeconds(seconds);

      [Fact]
      public void DefaultDuration_IsAboutAThirdOfASecondOrLess() {
         Assert.InRange(ZoomAnimator.DefaultDurationSeconds, 0.05, 1.0 / 3);
      }

      [Fact]
      public void NewAnimator_IsIdle() {
         var animator = Create();
         Assert.False(animator.IsAnimating);
         Assert.Equal(animator.Target, animator.Update());
      }

      [Fact]
      public void Start_BeginsAtTheOldScale() {
         var animator = Create();
         animator.Start(1, 2);
         Assert.True(animator.IsAnimating);
         Assert.Equal(1, animator.Current);
         Assert.Equal(2, animator.Target);
         Assert.Equal(1, animator.Update());
         Assert.True(animator.IsAnimating);
      }

      [Fact]
      public void Update_AfterTheDuration_IsTheExactTargetAndStops() {
         var animator = Create(0.25);
         animator.Start(1, 3);
         Wait(0.25);
         Assert.Equal(3, animator.Update());
         Assert.False(animator.IsAnimating);
         Assert.Equal(3, animator.Current);
      }

      [Fact]
      public void Update_AFrameThatComesLate_IsTheExactTarget() {
         var animator = Create(0.25);
         animator.Start(0.5, 0.25);
         Wait(30); // the app was busy (or the window was dragged)
         Assert.Equal(0.25, animator.Update());
         Assert.False(animator.IsAnimating);
      }

      [Fact]
      public void Update_ComesBackWithoutAnimating_StaysAtTheTarget() {
         var animator = Create();
         animator.Start(1, 2);
         Wait(1);
         animator.Update();
         Wait(1);
         Assert.Equal(2, animator.Update());
         Assert.False(animator.IsAnimating);
      }

      [Fact]
      public void ZoomingIn_ScaleGrowsFrameByFrameAndNeverOvershoots() {
         var animator = Create(0.25);
         animator.Start(1, 2);
         var scales = new List<double>();
         for (int i = 0; i < 20 && animator.IsAnimating; i++) {
            Wait(0.016); // 60 frames per second
            scales.Add(animator.Update());
         }
         Assert.False(animator.IsAnimating);
         Assert.Equal(scales.OrderBy(scale => scale), scales);
         Assert.All(scales, scale => Assert.InRange(scale, 1, 2));
         Assert.Equal(2, scales.Last());
         Assert.True(scales.Count >= 10, "a quarter of a second should be several frames");
         Assert.True(scales.Count <= 17, "and it should not take longer than the duration");
      }

      [Fact]
      public void ZoomingOut_ScaleShrinksFrameByFrameAndNeverUndershoots() {
         var animator = Create(0.25);
         animator.Start(4, 3);
         var scales = new List<double>();
         for (int i = 0; i < 20 && animator.IsAnimating; i++) {
            Wait(0.016);
            scales.Add(animator.Update());
         }
         Assert.Equal(scales.OrderByDescending(scale => scale), scales);
         Assert.All(scales, scale => Assert.InRange(scale, 3, 4));
         Assert.Equal(3, scales.Last());
      }

      [Fact]
      public void EaseOut_StartsFastAndEndsSlow() {
         Assert.Equal(0, ZoomAnimator.EaseOut(0));
         Assert.Equal(1, ZoomAnimator.EaseOut(1));
         Assert.Equal(0, ZoomAnimator.EaseOut(-1));
         Assert.Equal(1, ZoomAnimator.EaseOut(2));
         Assert.True(ZoomAnimator.EaseOut(0.5) > 0.5, "ease-out is ahead of a straight line at the halfway point");
         Assert.True(ZoomAnimator.EaseOut(0.1) > 0.1);

         var first = ZoomAnimator.EaseOut(0.1) - ZoomAnimator.EaseOut(0);
         var last = ZoomAnimator.EaseOut(1) - ZoomAnimator.EaseOut(0.9);
         Assert.True(first > last * 3, "the first tenth covers much more of the zoom than the last tenth");
      }

      [Fact]
      public void EaseOut_NeverGoesBackwards() {
         var previous = 0.0;
         for (int i = 1; i <= 100; i++) {
            var value = ZoomAnimator.EaseOut(i / 100.0);
            Assert.True(value >= previous);
            previous = value;
         }
      }

      [Fact]
      public void Retarget_StartsFromTheScaleOnScreen_SoThePictureDoesNotJump() {
         var animator = Create(0.25);
         animator.Start(1, 2);
         Wait(0.1);
         var onScreen = animator.Current;
         Assert.InRange(onScreen, 1.2, 1.99);

         animator.Retarget(3);

         Assert.Equal(onScreen, animator.Current);
         Assert.Equal(3, animator.Target);
         Assert.True(animator.IsAnimating);
         Wait(0.05);
         Assert.True(animator.Update() > onScreen);
         Wait(0.25);
         Assert.Equal(3, animator.Update());
         Assert.False(animator.IsAnimating);
      }

      [Fact]
      public void Retarget_BackTheOtherWay_TurnsAround() {
         var animator = Create(0.25);
         animator.Start(1, 2);
         Wait(0.1);
         var onScreen = animator.Current;
         animator.Retarget(1);
         Wait(0.05);
         Assert.True(animator.Update() < onScreen);
         Wait(1);
         Assert.Equal(1, animator.Update());
      }

      [Fact]
      public void Retarget_WhenIdle_StartsFromTheScaleItStoppedAt() {
         var animator = Create();
         animator.Snap(5);
         animator.Retarget(6);
         Assert.True(animator.IsAnimating);
         Assert.Equal(5, animator.Current);
         Assert.Equal(6, animator.Target);
      }

      [Fact]
      public void Retarget_ManyTimesInARow_EndsOnTheLastTarget() {
         var animator = Create(0.25);
         animator.Start(1, 2);
         // a fast scroll of the wheel: another click every 40 milliseconds
         for (double target = 3; target <= 6; target++) {
            Wait(0.04);
            animator.Update();
            animator.Retarget(target);
         }
         Assert.Equal(6, animator.Target);
         Wait(0.5);
         Assert.Equal(6, animator.Update());
         Assert.False(animator.IsAnimating);
      }

      [Fact]
      public void ZeroDuration_TurnsTheAnimationOff() {
         var animator = Create(0);
         animator.Start(1, 2);
         Assert.False(animator.IsAnimating);
         Assert.Equal(2, animator.Current);
         Assert.Equal(2, animator.Update());
      }

      [Fact]
      public void NegativeDuration_IsTreatedAsOff() {
         var animator = Create(-1);
         animator.Start(1, 2);
         Assert.False(animator.IsAnimating);
         Assert.Equal(2, animator.Update());
      }

      [Fact]
      public void Start_ToTheSameScale_DoesNotAnimate() {
         var animator = Create();
         animator.Start(2, 2);
         Assert.False(animator.IsAnimating);
         Assert.Equal(2, animator.Update());
      }

      [Fact]
      public void Snap_StopsAnAnimationRightWhereYouTellIt() {
         var animator = Create();
         animator.Start(1, 4);
         Wait(0.05);
         animator.Snap(2);
         Assert.False(animator.IsAnimating);
         Assert.Equal(2, animator.Current);
         Assert.Equal(2, animator.Target);
         Wait(1);
         Assert.Equal(2, animator.Update());
      }

      [Fact]
      public void TheDefaultClock_IsARealClock() {
         var animator = new ZoomAnimator();
         animator.Start(1, 2);
         Assert.True(animator.IsAnimating);
         var scale = animator.Update();
         Assert.InRange(scale, 1, 2);
      }

      #region NextScale

      [Theory]
      [InlineData(1.0, 2.0)]
      [InlineData(2.0, 3.0)]
      [InlineData(9.0, 10.0)]
      [InlineData(0.5, 1.0)]
      [InlineData(0.25, 0.5)]
      [InlineData(0.0625, 0.125)]
      public void NextScale_ZoomingIn(double scale, double expected) {
         Assert.Equal(expected, BlockMapViewModel.NextScale(scale, true));
      }

      [Theory]
      [InlineData(10.0, 9.0)]
      [InlineData(2.0, 1.0)]
      [InlineData(1.0, 0.5)]
      [InlineData(0.5, 0.25)]
      [InlineData(0.125, 0.0625)]
      public void NextScale_ZoomingOut(double scale, double expected) {
         Assert.Equal(expected, BlockMapViewModel.NextScale(scale, false));
      }

      [Fact]
      public void NextScale_StopsAtTheLimits() {
         Assert.Equal(10, BlockMapViewModel.NextScale(10, true));
         Assert.Equal(0.0625, BlockMapViewModel.NextScale(0.0625, false));
      }

      [Fact]
      public void NextScale_InThenOut_ComesBack() {
         for (double scale = 0.125; scale <= 9; scale = BlockMapViewModel.NextScale(scale, true)) {
            Assert.Equal(scale, BlockMapViewModel.NextScale(BlockMapViewModel.NextScale(scale, true), false));
         }
      }

      #endregion
   }
}
