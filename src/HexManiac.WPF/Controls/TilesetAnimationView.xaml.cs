using HavenSoft.HexManiac.Core.ViewModels;
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace HavenSoft.HexManiac.WPF.Controls {
   public partial class TilesetAnimationView {
      private TilesetAnimationTab ViewModel => DataContext as TilesetAnimationTab;

      private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(1000.0 / 60) };

      public TilesetAnimationView() {
         InitializeComponent();
         timer.Tick += (sender, e) => ViewModel?.Tick();
         Loaded += (sender, e) => timer.Start();
         Unloaded += (sender, e) => timer.Stop();
      }

      private void TilesetClicked(object sender, MouseButtonEventArgs e) {
         var position = e.GetPosition(TilesetImage); // the image's own (unscaled) pixel coordinates
         ViewModel?.PickTile((int)position.X, (int)position.Y);
      }

      private void TilesetDragged(object sender, MouseEventArgs e) {
         if (e.LeftButton != MouseButtonState.Pressed) return;
         TilesetClicked(sender, null);
      }

      private void EntryClicked(object sender, MouseButtonEventArgs e) {
         if (sender is FrameworkElement element && element.DataContext is TilesetAnimationItem item && ViewModel != null) ViewModel.SelectedEntry = item;
      }

      private void DoorClicked(object sender, MouseButtonEventArgs e) {
         if (sender is FrameworkElement element && element.DataContext is DoorItem item && ViewModel != null) ViewModel.SelectedDoor = item;
      }

      private void DoorFrameClicked(object sender, MouseButtonEventArgs e) {
         if (sender is FrameworkElement element && element.DataContext is TilesetAnimationFrame frame && frame.Door != null && ViewModel != null) {
            ViewModel.SelectedDoor = frame.Door;
            frame.Door.EditFrame(frame);
            e.Handled = true;
         }
      }

      private void FrameClicked(object sender, MouseButtonEventArgs e) {
         if (sender is FrameworkElement element && element.DataContext is TilesetAnimationFrame frame && ViewModel != null) {
            ViewModel.SelectedEntry = frame.Owner;
            frame.Owner.EditFrame(frame);
            e.Handled = true;
         }
      }
   }
}
