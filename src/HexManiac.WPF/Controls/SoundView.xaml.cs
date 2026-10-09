using HavenSoft.HexManiac.Core.ViewModels;
using System;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Input;

namespace HavenSoft.HexManiac.WPF.Controls {
   public partial class SoundView {
      private SoundPlayer player;
      private MemoryStream playerStream;

      private SoundTab ViewModel => DataContext as SoundTab;

      public SoundView() {
         InitializeComponent();
         DataContextChanged += (sender, e) => {
            if (e.OldValue is SoundTab old) old.RequestPlayWav -= PlayWav;
            if (e.NewValue is SoundTab vm) vm.RequestPlayWav += PlayWav;
         };
         Unloaded += (sender, e) => StopPlayback();
      }

      private void PlayWav(object sender, byte[] wav) {
         try {
            StopPlayback();
            playerStream = new MemoryStream(wav);
            player = new SoundPlayer(playerStream);
            player.Play();
         } catch (Exception ex) {
            MessageBox.Show("Could not play the cry: " + ex.Message, "Hex Maniac Advance", MessageBoxButton.OK, MessageBoxImage.Warning);
         }
      }

      private void StopPlayback() {
         try {
            player?.Stop();
            player?.Dispose();
            playerStream?.Dispose();
         } catch (Exception) {
            // nothing to clean up
         }
         player = null;
         playerStream = null;
      }

      private void CryDoubleClick(object sender, MouseButtonEventArgs e) {
         if (ViewModel?.PlayCry.CanExecute(null) == true) ViewModel.PlayCry.Execute(null);
      }
   }
}
