using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels.Images;
using HavenSoft.HexManiac.Core.ViewModels.Map;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace HavenSoft.HexManiac.Core.ViewModels {
   /// <summary>
   /// A browsable grid of every overworld sprite in the ROM (first frame, facing down), with its index and name.
   /// Clicking an entry jumps the main tab to that entry of graphics.overworld.sprites.
   /// </summary>
   public class SpriteGalleryTab : ViewModelCore, ITabContent {
      private readonly ViewPort viewPort;
      private readonly IDataModel model;
      private readonly StubCommand close = new();

      public ObservableCollection<SpriteGalleryItem> Elements { get; } = new();

      public string Name => "Overworld Sprites";
      public bool SpartanMode { get; set; }
      public IDataModel Model => model;

      private string filter = string.Empty;
      public string Filter {
         get => filter;
         set {
            if (!TryUpdate(ref filter, value)) return;
            foreach (var item in Elements) item.MatchToFilter(filter);
         }
      }

      private double spriteScale = 2;
      public double SpriteScale {
         get => spriteScale;
         set => Set(ref spriteScale, value.LimitToRange(1, 4), old => { foreach (var item in Elements) item.SpriteScale = spriteScale; });
      }

      private string status = string.Empty;
      public string Status { get => status; private set => Set(ref status, value); }

      public bool IsMetadataOnlyChange => false;
      public ICommand Save { get; }
      public ICommand SaveAs { get; }
      public ICommand ExportBackup { get; } = new StubCommand();
      public ICommand Undo { get; }
      public ICommand Redo { get; }
      public ICommand Copy { get; } = new StubCommand();
      public ICommand DeepCopy { get; } = new StubCommand();
      public ICommand Diff => null;
      public ICommand DiffLeft => null;
      public ICommand DiffRight => null;
      public ICommand Clear { get; } = new StubCommand();
      public ICommand SelectAll { get; } = new StubCommand();
      public ICommand Goto { get; } = new StubCommand();
      public ICommand ResetAlignment { get; } = new StubCommand();
      public ICommand Back { get; } = new StubCommand();
      public ICommand Forward { get; } = new StubCommand();
      public ICommand Close => close;
      public bool CanDuplicate => false;
      public void Duplicate() { }

      private StubCommand refresh;
      public ICommand Refresh => StubCommand(ref refresh, Reload);

      public event EventHandler<string> OnError;
      public event EventHandler Closed;
      public event EventHandler<TabChangeRequestedEventArgs> RequestTabChange;
      event EventHandler<string> ITabContent.OnMessage { add { } remove { } }
      event EventHandler ITabContent.ClearMessage { add { } remove { } }
      event EventHandler<Action> ITabContent.RequestDelayedWork { add { } remove { } }
      event EventHandler ITabContent.RequestMenuClose { add { } remove { } }
      event EventHandler<Direction> ITabContent.RequestDiff { add { } remove { } }
      event EventHandler<CanDiffEventArgs> ITabContent.RequestCanDiff { add { } remove { } }
      event EventHandler<CanPatchEventArgs> ITabContent.RequestCanCreatePatch { add { } remove { } }
      event EventHandler<CanPatchEventArgs> ITabContent.RequestCreatePatch { add { } remove { } }
      event EventHandler ITabContent.RequestRefreshGotoShortcuts { add { } remove { } }

      public bool CanIpsPatchRight => false;
      public bool CanUpsPatchRight => false;
      public void IpsPatchRight() { }
      public void UpsPatchRight() { }

      public SpriteGalleryTab(ViewPort viewPort) {
         this.viewPort = viewPort;
         model = viewPort.Model;
         Save = viewPort.Save;
         SaveAs = viewPort.SaveAs;
         Undo = viewPort.Undo;
         Redo = viewPort.Redo;
         close.CanExecute = arg => true;
         close.Execute = arg => Closed?.Invoke(this, EventArgs.Empty);
         Reload();
      }

      public void Reload() {
         Elements.Clear();
         var ows = model.GetTable(HardcodeTablesModel.OverworldSprites);
         if (ows == null) { Status = $"This ROM has no {HardcodeTablesModel.OverworldSprites} table."; return; }
         var owTable = new ModelTable(model, ows.Start);
         var defaultOW = BlockMapViewModel.GetDefaultOW(model);
         var names = model.GetOptions(HardcodeTablesModel.OverworldSprites);
         int rendered = 0;
         for (int i = 0; i < ows.ElementCount; i++) {
            IPixelViewModel image;
            try {
               image = ObjectEventViewModel.Render(model, owTable, defaultOW, i, 0, () => -1);
            } catch (Exception) {
               image = defaultOW;
            }
            var isPlaceholder = ReferenceEquals(image, defaultOW);
            if (!isPlaceholder) rendered++;
            var name = names != null && i < names.Count ? names[i] : string.Empty;
            Elements.Add(new SpriteGalleryItem(i, ows.Start + ows.ElementLength * i, name, image, isPlaceholder) { SpriteScale = SpriteScale });
         }
         foreach (var item in Elements) item.MatchToFilter(filter);
         Status = $"{ows.ElementCount} overworld sprites ({rendered} with graphics)";
      }

      /// <summary>
      /// Jump the main tab to this sprite's entry in the overworld sprite table.
      /// </summary>
      public void Select(SpriteGalleryItem item) {
         if (item == null) return;
         foreach (var element in Elements) element.Selected = element == item;
         viewPort.Goto.Execute($"{HardcodeTablesModel.OverworldSprites}/{item.Index}");
         RequestTabChange?.Invoke(this, new TabChangeRequestedEventArgs(viewPort));
      }

      void ITabContent.Refresh() => Reload();
      public bool TryImport(LoadedFile file, IFileSystem fileSystem) => false;
   }

   public class SpriteGalleryItem : ViewModelCore, IPixelViewModel {
      private readonly IPixelViewModel image;
      public int Index { get; }
      public int Address { get; }
      public string Name { get; }
      public bool IsPlaceholder { get; }
      public string Label => string.IsNullOrEmpty(Name) || Name == Index.ToString() ? $"{Index}" : $"{Index} {Name}";

      public short Transparent => image.Transparent;
      public int PixelWidth => image.PixelWidth;
      public int PixelHeight => image.PixelHeight;
      public short[] PixelData => image.PixelData;

      private double spriteScale = 2;
      public double SpriteScale { get => spriteScale; set => Set(ref spriteScale, value); }

      private bool isFilteredOut;
      public bool IsFilteredOut { get => isFilteredOut; set => TryUpdate(ref isFilteredOut, value); }

      private bool selected;
      public bool Selected { get => selected; set => TryUpdate(ref selected, value); }

      public SpriteGalleryItem(int index, int address, string name, IPixelViewModel image, bool isPlaceholder) {
         Index = index;
         Address = address;
         Name = name ?? string.Empty;
         this.image = image;
         IsPlaceholder = isPlaceholder;
      }

      public void MatchToFilter(string filter) {
         if (string.IsNullOrWhiteSpace(filter)) { IsFilteredOut = false; return; }
         var terms = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
         IsFilteredOut = !terms.All(term => Index.ToString() == term || Name.Contains(term, StringComparison.OrdinalIgnoreCase));
      }
   }
}
