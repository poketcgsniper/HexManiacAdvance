using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels.Images;
using HavenSoft.HexManiac.Core.ViewModels.Map;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace HavenSoft.HexManiac.Core.ViewModels.Tools {
   /// <summary>
   /// Shown in the table tool under an overworld sprite's fields: the first frame of every overworld sprite, so you can
   /// see what you're editing and jump to another sprite with one click. Also lets you rename the current sprite:
   /// the names live in the 'objecteventgfx' list, which is saved with the ROM's .toml.
   /// </summary>
   public class SpriteGalleryElementViewModel : ViewModelCore, IArrayElementViewModel {
      public const string NameListName = "objecteventgfx";
      private const string CacheKey = "overworld-sprite-gallery";

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

      private readonly ObservableCollection<SpriteGalleryItem> elements = new();
      private bool loaded;
      /// <summary>Rendered on first use: the table tool creates and discards these as the cursor moves.</summary>
      public ObservableCollection<SpriteGalleryItem> Elements { get { if (!loaded) Load(); return elements; } }
      public int CurrentIndex { get; private set; }

      private string currentName = string.Empty;
      /// <summary>The current sprite's name. Changing it updates the name list that every dropdown and the gallery use.</summary>
      public string CurrentName {
         get => currentName;
         set {
            if (currentName == value) return;
            currentName = value ?? string.Empty;
            NotifyPropertyChanged();
            RenameCurrent(currentName);
         }
      }

      private string filter = string.Empty;
      public string Filter {
         get => filter;
         set {
            if (!TryUpdate(ref filter, value ?? string.Empty)) return;
            foreach (var item in elements) item.MatchToFilter(filter);
         }
      }

      private double spriteScale = 2;
      public double SpriteScale {
         get => spriteScale;
         set => Set(ref spriteScale, value.LimitToRange(1, 3), old => { foreach (var item in elements) item.SpriteScale = spriteScale; });
      }

      private string status = string.Empty;
      public string Status { get => status; private set => Set(ref status, value); }

      public ICommand OpenGallery { get; }
      public ICommand Refresh { get; }
      /// <summary>Parameter: the SpriteGalleryItem that was clicked.</summary>
      public ICommand SelectCommand { get; }

      public SpriteGalleryElementViewModel(ViewPort viewPort, int index) {
         this.viewPort = viewPort;
         model = viewPort.Model;
         CurrentIndex = index;
         OpenGallery = new StubCommand { CanExecute = arg => true, Execute = arg => viewPort.OpenSpriteGalleryTab() };
         Refresh = new StubCommand { CanExecute = arg => true, Execute = arg => { model.ClearCacheScope(); Load(); } };
         SelectCommand = new StubCommand { CanExecute = arg => true, Execute = arg => Select(arg as SpriteGalleryItem) };
         var names = model.GetOptions(HardcodeTablesModel.OverworldSprites);
         currentName = names != null && index < names.Count ? names[index] : string.Empty;
      }

      /// <summary>Render (or fetch from the cache) every sprite's first frame.</summary>
      private void Load() {
         loaded = true;
         elements.Clear();
         var ows = model.GetTable(HardcodeTablesModel.OverworldSprites);
         if (ows == null) return;
         var names = model.GetOptions(HardcodeTablesModel.OverworldSprites);
         var images = model.CurrentCacheScope.GetOrAdd(CacheKey, () => RenderAll(model, ows));
         for (int i = 0; i < ows.ElementCount && i < images.Count; i++) {
            var name = names != null && i < names.Count ? names[i] : string.Empty;
            var item = new SpriteGalleryItem(i, ows.Start + ows.ElementLength * i, name, images[i].image, images[i].isPlaceholder) { SpriteScale = spriteScale, Selected = i == CurrentIndex };
            item.MatchToFilter(filter);
            elements.Add(item);
         }
         currentName = names != null && CurrentIndex < names.Count ? names[CurrentIndex] : string.Empty;
         NotifyPropertyChanged(nameof(CurrentName));
         Status = $"{ows.ElementCount} sprites";
      }

      private static IReadOnlyList<(IPixelViewModel image, bool isPlaceholder)> RenderAll(IDataModel model, ITableRun ows) {
         var results = new List<(IPixelViewModel, bool)>();
         var owTable = new ModelTable(model, ows.Start);
         var defaultOW = BlockMapViewModel.GetDefaultOW(model);
         for (int i = 0; i < ows.ElementCount; i++) {
            IPixelViewModel image;
            try {
               image = ObjectEventViewModel.Render(model, owTable, defaultOW, i, 0, () => -1);
            } catch (Exception) {
               image = defaultOW;
            }
            results.Add((image, ReferenceEquals(image, defaultOW)));
         }
         return results;
      }

      /// <summary>Jump the table tool to another sprite.</summary>
      public void Select(SpriteGalleryItem item) {
         if (item == null) return;
         viewPort.Goto.Execute($"{HardcodeTablesModel.OverworldSprites}/{item.Index}");
      }

      private void RenameCurrent(string name) {
         name = (name ?? string.Empty).Trim();
         if (!model.TryGetList(NameListName, out var list)) return;
         var names = list.ToList();
         while (names.Count <= CurrentIndex) names.Add(names.Count.ToString());
         if (string.IsNullOrEmpty(name)) name = CurrentIndex.ToString();
         // list entries can't have spaces: they're used as identifiers in scripts and dropdowns
         name = name.Replace(' ', '_');
         if (names[CurrentIndex] == name) return;
         names[CurrentIndex] = name;
         model.SetList(viewPort.CurrentChange, NameListName, names, list.Comments, StoredList.GenerateHash(names));
         model.ClearCacheScope();
         viewPort.ChangeHistory.ChangeCompleted();
         var item = elements.FirstOrDefault(element => element.Index == CurrentIndex);
         if (item != null) item.Name = name;
         if (currentName != name) { currentName = name; NotifyPropertyChanged(nameof(CurrentName)); }
      }

      public bool TryCopy(IArrayElementViewModel other) {
         if (other is not SpriteGalleryElementViewModel gallery) return false;
         if (gallery.model != model) return false;
         CurrentIndex = gallery.CurrentIndex;
         foreach (var item in elements) item.Selected = item.Index == CurrentIndex;
         var names = model.GetOptions(HardcodeTablesModel.OverworldSprites);
         currentName = names != null && CurrentIndex < names.Count ? names[CurrentIndex] : string.Empty;
         NotifyPropertyChanged(nameof(CurrentName));
         NotifyPropertyChanged(nameof(CurrentIndex));
         // the sprite being edited may have changed: re-render just that one
         var ows = model.GetTable(HardcodeTablesModel.OverworldSprites);
         var current = elements.FirstOrDefault(element => element.Index == CurrentIndex);
         if (ows != null && current != null) {
            try {
               var defaultOW = BlockMapViewModel.GetDefaultOW(model);
               var image = ObjectEventViewModel.Render(model, new ModelTable(model, ows.Start), defaultOW, CurrentIndex, 0, () => -1);
               current.Replace(image, ReferenceEquals(image, defaultOW));
            } catch (Exception) {
               // keep the old picture
            }
         }
         Visible = other.Visible;
         return true;
      }
   }
}
