using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels.Images;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace HavenSoft.HexManiac.Core.ViewModels.Tools {
   /// <summary>
   /// Shown in the table tool under a field effect's fields (grass rustle, shadows, ripples, dust, sparkles...): the effect's name, every frame of its picture
   /// drawn with the palette the game uses for it, which palette that is (with a button that jumps to it, to edit its colours), and a gallery
   /// of all the field effects to jump to another one with a click.
   /// </summary>
   public class FieldEffectElementViewModel : ViewModelCore, IArrayElementViewModel {
      /// <summary>The room a picture gets in the gallery, in screen pixels (the pictures are drawn as big as fit, 1x to 3x).</summary>
      public const double CellWidth = 64, CellHeight = 40;

      private readonly ViewPort viewPort;
      private readonly IDataModel model;

      private string theme; public string Theme { get => theme; set => Set(ref theme, value); }
      public bool IsInError => false;
      public string ErrorText => string.Empty;
      public int ZIndex => 0;

      private bool visible = true;
      public bool Visible { get => visible; set => Set(ref visible, value); }

      event EventHandler IArrayElementViewModel.DataChanged { add { } remove { } }
      event EventHandler IArrayElementViewModel.DataSelected { add { } remove { } }

      public int CurrentIndex { get; private set; }

      private string title = string.Empty;
      /// <summary>"4: TALL_GRASS"</summary>
      public string Title { get => title; private set => Set(ref title, value); }

      private string info = string.Empty;
      /// <summary>Size, number of frames and the tags, in a few lines.</summary>
      public string Info { get => info; private set => Set(ref info, value); }

      private string paletteTitle = string.Empty;
      /// <summary>Which palette the effect is drawn with and where it is stored.</summary>
      public string PaletteTitle { get => paletteTitle; private set => Set(ref paletteTitle, value); }

      private IPixelViewModel paletteImage;
      /// <summary>The palette's sixteen colours as a strip, or null if the effect has no palette to show.</summary>
      public IPixelViewModel PaletteImage { get => paletteImage; private set { paletteImage = value; NotifyPropertyChanged(); } }

      public bool HasPalette => palette != null;

      private FieldEffectPalette palette;

      /// <summary>Every frame of the picture, as the game draws it.</summary>
      public ObservableCollection<IPixelViewModel> Frames { get; } = new();

      public bool HasFrames => Frames.Count > 0;

      private readonly ObservableCollection<SpriteGalleryItem> elements = new();
      private bool loaded;
      /// <summary>Listed on first use: the table tool creates and discards these as the cursor moves.</summary>
      public ObservableCollection<SpriteGalleryItem> Elements { get { if (!loaded) Load(); return elements; } }

      private string filter = string.Empty;
      public string Filter {
         get => filter;
         set {
            if (!TryUpdate(ref filter, value ?? string.Empty)) return;
            foreach (var item in elements) item.MatchToFilter(filter);
         }
      }

      private string status = string.Empty;
      public string Status { get => status; private set => Set(ref status, value); }

      public ICommand OpenPalette { get; }
      public ICommand Refresh { get; }
      /// <summary>Parameter: the SpriteGalleryItem that was clicked.</summary>
      public ICommand SelectCommand { get; }

      public FieldEffectElementViewModel(ViewPort viewPort, int index) {
         this.viewPort = viewPort;
         model = viewPort.Model;
         OpenPalette = new StubCommand { CanExecute = arg => palette != null, Execute = arg => { if (palette != null) viewPort.Goto.Execute(palette.GotoDestination); } };
         Refresh = new StubCommand { CanExecute = arg => true, Execute = arg => { model.ClearCacheScope(); Load(); ShowEffect(CurrentIndex); } };
         SelectCommand = new StubCommand { CanExecute = arg => true, Execute = arg => Select(arg as SpriteGalleryItem) };
         ShowEffect(index);
      }

      /// <summary>Fills in the name, the info, the palette and the frames of the effect.</summary>
      private void ShowEffect(int index) {
         CurrentIndex = index;
         NotifyPropertyChanged(nameof(CurrentIndex));
         var effect = FieldEffects.Describe(model, index);
         Title = effect == null || string.IsNullOrEmpty(effect.Name) ? $"Field effect {index}" : $"{index}: {effect.Name}";
         Info = DescribeInfo(effect);
         palette = effect?.Palette;
         PaletteTitle = DescribePalette(effect);
         PaletteImage = palette == null ? null : FieldEffects.CreatePaletteStrip(palette.Colors);
         NotifyPropertyChanged(nameof(HasPalette));
         Frames.Clear();
         if (effect != null) {
            var scale = effect.Width > 0 ? Math.Max(1, Math.Min(3, (int)(CellWidth * 3 / effect.Width))) : 2;
            foreach (var frame in FieldEffects.RenderFrames(model, effect, scale)) Frames.Add(frame);
         }
         NotifyPropertyChanged(nameof(HasFrames));
         (OpenPalette as StubCommand)?.RaiseCanExecuteChanged();
      }

      private static string DescribeInfo(FieldEffectInfo effect) {
         if (effect == null) return string.Empty;
         if (!effect.HasPicture) return "This effect has no picture (an empty template).";
         var frames = effect.FrameCount == 1 ? "1 frame" : $"{effect.FrameCount} frames";
         return $"{effect.Width}x{effect.Height} pixels, {frames}\nTile tag {(effect.TileTag < 0 ? "?" : effect.TileTag.ToString("X4"))}, palette tag {effect.PaletteTagText}";
      }

      private static string DescribePalette(FieldEffectInfo effect) {
         if (effect == null || !effect.HasPicture) return string.Empty;
         var palette = effect.Palette;
         if (palette == null) return effect.PaletteTag == FieldEffects.NoTag ? "No palette of its own: the game draws it with the palette of the object it belongs to." : $"Palette {effect.PaletteTagText} is not in any palette table.";
         var place = $"{palette.TableName} #{palette.Index}";
         if (palette.IsPreviewOnly) return $"No palette of its own (the game uses the palette of the object it belongs to). Drawn here with the {palette.Title}, {place}.";
         return $"Palette {palette.Tag:X4}: the {palette.Title}, {place}.";
      }

      /// <summary>
      /// List every effect. Nothing is drawn here: each picture is drawn when the view first asks for it (see <see cref="FieldEffectRenderer"/>).
      /// </summary>
      private void Load() {
         loaded = true;
         elements.Clear();
         selectedItem = null;
         var tableName = FieldEffects.FindTableName(model);
         var count = FieldEffects.Count(model);
         if (tableName == null) { Status = string.Empty; return; }
         var pictures = renderer = FieldEffectRenderer.Get(model);
         var table = model.GetTable(tableName);
         for (int i = 0; i < count; i++) {
            var index = i;
            var (width, height) = FieldEffects.FrameSize(model, index);
            var scale = width > 0 && height > 0 ? Math.Max(1, Math.Min(3, (int)Math.Min(CellWidth / width, CellHeight / height))) : 2;
            var item = new SpriteGalleryItem(index, table.Start + table.ElementLength * index, FieldEffects.NameOf(model, index), () => pictures.Get(index)) { SpriteScale = scale, SelectCommand = SelectCommand };
            item.MatchToFilter(filter);
            elements.Add(item);
         }
         if (CurrentIndex >= 0 && CurrentIndex < elements.Count) { selectedItem = elements[CurrentIndex]; selectedItem.Selected = true; }
         Status = $"{count} field effects";
      }

      private SpriteGalleryItem selectedItem;
      private FieldEffectRenderer renderer;

      /// <summary>Jump the table tool to another effect.</summary>
      public void Select(SpriteGalleryItem item) {
         if (item == null) return;
         var tableName = FieldEffects.FindTableName(model);
         if (tableName == null) return;
         viewPort.Goto.Execute($"{tableName}/{item.Index}");
      }

      public bool TryCopy(IArrayElementViewModel other) {
         if (other is not FieldEffectElementViewModel next) return false;
         if (next.model != model) return false;
         // the effect (and its palette, and its frames) may have been edited, so draw it again, but only this one
         ShowEffect(next.CurrentIndex);
         if (loaded) {
            var current = CurrentIndex >= 0 && CurrentIndex < elements.Count ? elements[CurrentIndex] : null;
            if (!ReferenceEquals(selectedItem, current)) {
               if (selectedItem != null) selectedItem.Selected = false;
               selectedItem = current;
               if (current != null) current.Selected = true;
            }
            if (selectedItem != null && selectedItem.IsRendered) {
               var redrawn = renderer?.Redraw(CurrentIndex);
               if (redrawn != null) selectedItem.Replace(redrawn.Value.image, redrawn.Value.isPlaceholder);
            }
         }
         Visible = other.Visible;
         return true;
      }
   }
}
