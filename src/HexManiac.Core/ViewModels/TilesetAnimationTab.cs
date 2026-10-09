using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Map;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.Models.Runs.Sprites;
using HavenSoft.HexManiac.Core.ViewModels.Images;
using HavenSoft.HexManiac.Core.ViewModels.Map;
using HexManiac.Core.Models.Runs.Sprites;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace HavenSoft.HexManiac.Core.ViewModels {
   /// <summary>
   /// The "Animated Tiles" editor: pick a map's tileset, click the tiles that should animate, choose how many frames and how fast,
   /// then draw or import the frames. The tab writes the animation table and the code that plays it.
   /// </summary>
   public class TilesetAnimationTab : ViewModelCore, ITabContent {
      private readonly IFileSystem fileSystem;
      private readonly ViewPort viewPort;
      private readonly IDataModel model;
      private readonly TilesetAnimations animations;
      private readonly DoorAnimations doors;
      private readonly StubCommand close = new();
      private TilesetAnimationConstants constants;

      #region ITabContent

      public string Name => "Animated Tiles";
      public bool SpartanMode { get; set; }
      public IDataModel Model => model;
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

      public event EventHandler<string> OnError;
      public event EventHandler<string> OnMessage;
      public event EventHandler Closed;
      public event EventHandler<TabChangeRequestedEventArgs> RequestTabChange;
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
      void ITabContent.Refresh() => Reload();
      public bool TryImport(LoadedFile file, IFileSystem fileSystem) => false;

      #endregion

      public static bool IsSupported(IDataModel model) => TilesetAnimations.IsSupported(model) && model.GetTable(HardcodeTablesModel.MapBankTable) != null;

      private string status = string.Empty;
      public string Status { get => status; private set => Set(ref status, value); }

      public TilesetAnimationTab(IFileSystem fileSystem, ViewPort viewPort, int group, int map, bool secondary) {
         this.fileSystem = fileSystem;
         this.viewPort = viewPort;
         model = viewPort.Model;
         Save = viewPort.Save;
         SaveAs = viewPort.SaveAs;
         Undo = viewPort.Undo;
         Redo = viewPort.Redo;
         close.CanExecute = arg => true;
         close.Execute = arg => Closed?.Invoke(this, EventArgs.Empty);
         animations = new TilesetAnimations(model, () => viewPort.CurrentChange, viewPort.Tools.CodeTool.Parser);
         doors = new DoorAnimations(model, () => viewPort.CurrentChange);
         TilesetAnimationConstants.TryRead(model, out constants);
         LoadMaps();
         isSecondary = secondary;
         selectedMap = Maps.FirstOrDefault(option => option.Group == group && option.Map == map) ?? Maps.FirstOrDefault();
         Reload();
      }

      #region Map / tileset selection

      public ObservableCollection<MapOption> Maps { get; } = new();

      private MapOption selectedMap;
      public MapOption SelectedMap {
         get => selectedMap;
         set { if (selectedMap == value || value == null) return; selectedMap = value; NotifyPropertyChanged(); Reload(); }
      }

      private bool isSecondary;
      public bool IsSecondary { get => isSecondary; set { if (isSecondary == value) return; isSecondary = value; NotifyPropertyChanged(); NotifyPropertyChanged(nameof(IsPrimary)); Reload(); } }
      public bool IsPrimary { get => !isSecondary; set => IsSecondary = !value; }

      private void LoadMaps() {
         Maps.Clear();
         try {
            var all = AllMapsModel.Create(model);
            for (int g = 0; g < all.Count; g++) {
               var bank = all[g];
               if (bank == null) continue;
               for (int m = 0; m < bank.Count; m++) {
                  Maps.Add(new MapOption(g, m, BlockMapViewModel.MapIDToText(model, g, m)));
               }
            }
         } catch (Exception) {
            // a broken map table just leaves the list short
         }
      }

      private MiniBlocksetModel CurrentBlockset() {
         if (selectedMap == null) return null;
         var all = AllMapsModel.Create(model, () => viewPort.CurrentChange);
         var bank = all[selectedMap.Group];
         var mapModel = bank?[selectedMap.Map];
         var layout = mapModel?.Layout;
         if (layout == null) return null;
         return isSecondary ? layout.SecondaryBlockset : layout.PrimaryBlockset;
      }

      #endregion

      #region Tileset image

      private IPixelViewModel tilesetImage = new ReadonlyPixelViewModel(128, 256);
      public IPixelViewModel TilesetImage { get => tilesetImage; private set { tilesetImage = value; NotifyPropertyChanged(); } }

      private double spriteScale = 2;
      public double SpriteScale {
         get => spriteScale;
         set => Set(ref spriteScale, value.LimitToRange(1, 4), old => {
            foreach (var entry in Entries) entry.SpriteScale = spriteScale;
            if (tilesetImage is CanvasPixelViewModel canvas) canvas.SpriteScale = spriteScale;
            NotifySelectionChanged();
         });
      }

      public int TilesPerRow => 16;
      public int TileCountInTileset { get; private set; }
      private int[][,] tiles;          // this tileset's tiles only
      private short[][] palettes;      // all 13 palettes (primary + secondary)
      private int[] tilePalette;       // best-guess palette for each tile
      private byte[] rawTiles;         // 4bpp tile data of the tileset

      private string tilesetDescription = string.Empty;
      public string TilesetDescription { get => tilesetDescription; private set => Set(ref tilesetDescription, value); }

      private string animationDescription = string.Empty;
      public string AnimationDescription { get => animationDescription; private set => Set(ref animationDescription, value); }

      #endregion

      #region New animation

      private int firstTile;
      /// <summary>The first tile of the new animation (click the tileset image to pick it).</summary>
      public int FirstTile { get => firstTile; set { Set(ref firstTile, value.LimitToRange(0, Math.Max(0, TileCountInTileset - 1))); NotifySelectionChanged(); } }

      private void NotifySelectionChanged() {
         NotifyPropertyChanged(nameof(SelectionPreview));
         NotifyPropertyChanged(nameof(SelectionText));
         NotifyPropertyChanged(nameof(SelectionX));
         NotifyPropertyChanged(nameof(SelectionY));
         NotifyPropertyChanged(nameof(SelectionWidth));
         NotifyPropertyChanged(nameof(SelectionHeight));
      }

      /// <summary>Position/size (in scaled pixels) of the highlight drawn over the tileset image for the chosen tiles (first row of them only).</summary>
      public double SelectionX => (firstTile % TilesPerRow) * 8 * spriteScale;
      public double SelectionY => (firstTile / TilesPerRow) * 8 * spriteScale;
      public double SelectionWidth => Math.Min(tileCount, TilesPerRow - firstTile % TilesPerRow) * 8 * spriteScale;
      public double SelectionHeight => 8 * spriteScale;

      private int tileCount = 4;
      public int TileCount { get => tileCount; set { Set(ref tileCount, value.LimitToRange(1, 64)); NotifySelectionChanged(); } }

      private int frameCount = 4;
      public int FrameCount { get => frameCount; set => Set(ref frameCount, value.LimitToRange(1, 64)); }

      private int speed = 4;
      /// <summary>The frame changes every 2^Speed game frames (4 = every 16 frames, about 4 times a second).</summary>
      public int Speed { get => speed; set { Set(ref speed, value.LimitToRange(0, TilesetAnimations.MaxTimer)); NotifyPropertyChanged(nameof(SpeedText)); } }
      public string SpeedText => DescribeSpeed(speed);

      public static string DescribeSpeed(int timer) {
         var frames = 1 << timer;
         return $"every {frames} game frame{(frames == 1 ? "" : "s")} ({60.0 / frames:0.#} changes per second)";
      }

      public string SelectionText => $"Tiles {firstTile} to {Math.Min(TileCountInTileset - 1, firstTile + tileCount - 1)}";
      public IPixelViewModel SelectionPreview => RenderTileRow(firstTile, tileCount, rawTiles, scale: Math.Max(2, spriteScale));

      /// <summary>Called by the view when the user clicks a tile in the tileset image (pixel coordinates, unscaled).</summary>
      public void PickTile(int x, int y) {
         if (tiles == null) return;
         var tile = (y / 8) * TilesPerRow + (x / 8);
         if (tile < 0 || tile >= TileCountInTileset) return;
         FirstTile = tile;
      }

      private StubCommand addAnimation;
      public ICommand AddAnimation => StubCommand(ref addAnimation, ExecuteAddAnimation, () => tiles != null && constants != null);

      private void ExecuteAddAnimation() {
         var blockset = CurrentBlockset();
         if (blockset == null || constants == null) return;
         try {
            var table = animations.EnsureTable(blockset.Start, isSecondary, constants, out _);
            animations.AddEntry(table, firstTile, tileCount, frameCount, speed, isSecondary, constants, rawTiles);
            viewPort.ChangeHistory.ChangeCompleted();
            viewPort.Refresh();
            Reload();
            SelectedEntry = Entries.LastOrDefault();
            OnMessage?.Invoke(this, $"Added an animation for tiles {firstTile}-{firstTile + tileCount - 1} with {frameCount} frames. Every frame starts as a copy of the tiles: edit or import the frames to make it move.");
         } catch (Exception e) {
            OnError?.Invoke(this, "Could not add the animation: " + e.Message);
         }
      }

      #endregion

      #region Existing animations

      public ObservableCollection<TilesetAnimationItem> Entries { get; } = new();

      private int tickCount;
      /// <summary>Called by the view about 60 times a second (once per game frame): advance every live preview the way the game would.</summary>
      public void Tick() {
         tickCount++;
         foreach (var entry in Entries) entry.Tick(tickCount);
         foreach (var door in Doors) door.Tick();
      }

      private TilesetAnimationItem selectedEntry;
      public TilesetAnimationItem SelectedEntry {
         get => selectedEntry;
         set {
            if (selectedEntry == value) return;
            if (selectedEntry != null) selectedEntry.Selected = false;
            selectedEntry = value;
            if (selectedEntry != null) selectedEntry.Selected = true;
            NotifyPropertyChanged();
            NotifyPropertyChanged(nameof(HasSelectedEntry));
            removeEntry?.RaiseCanExecuteChanged();
            importFrames?.RaiseCanExecuteChanged();
            exportFrames?.RaiseCanExecuteChanged();
         }
      }
      public bool HasSelectedEntry => selectedEntry != null;

      private StubCommand removeEntry, importFrames, exportFrames, gotoTable;
      public ICommand RemoveEntry => StubCommand(ref removeEntry, ExecuteRemoveEntry, () => selectedEntry != null);
      public ICommand ImportFrames => StubCommand(ref importFrames, ExecuteImportFrames, () => selectedEntry != null);
      public ICommand ExportFrames => StubCommand(ref exportFrames, ExecuteExportFrames, () => selectedEntry != null);
      public ICommand GotoTable => StubCommand(ref gotoTable, ExecuteGotoTable, () => Entries.Count > 0);

      private void ExecuteRemoveEntry() {
         var blockset = CurrentBlockset();
         if (selectedEntry == null || blockset == null) return;
         if (!animations.TryGetTable(blockset.Start, out var table, out _)) return;
         animations.RemoveEntry(table, selectedEntry.Entry.Index);
         viewPort.ChangeHistory.ChangeCompleted();
         viewPort.Refresh();
         Reload();
      }

      public void SetEntrySpeed(TilesetAnimationItem item, int timer) {
         var blockset = CurrentBlockset();
         if (item == null || blockset == null || !animations.TryGetTable(blockset.Start, out var table, out _)) return;
         animations.SetTimer(table, item.Entry.Index, timer);
         viewPort.ChangeHistory.ChangeCompleted();
         item.Refresh(animations.ReadEntries(table, isSecondary, constants)[item.Entry.Index], this);
      }

      public void SetEntryFrameCount(TilesetAnimationItem item, int count) {
         var blockset = CurrentBlockset();
         if (item == null || blockset == null || !animations.TryGetTable(blockset.Start, out var table, out _)) return;
         animations.SetFrameCount(table, item.Entry.Index, count);
         viewPort.ChangeHistory.ChangeCompleted();
         viewPort.Refresh();
         var index = item.Entry.Index;
         Reload();
         SelectedEntry = index < Entries.Count ? Entries[index] : null;
      }

      /// <summary>Open the frame in the image editor (with the palette these tiles use), where it can be drawn on or imported over.</summary>
      public void EditFrame(TilesetAnimationItem item, int frame) {
         if (item == null || frame < 0 || frame >= item.Entry.FrameCount) return;
         var blockset = CurrentBlockset();
         var frameAddress = blockset == null ? -1 : animations.EnsureFrameFormat(blockset.Start, item.Entry, frame);
         if (frameAddress < 0) frameAddress = model.ReadPointer(item.Entry.FramesAddress + 4 * frame);
         if (frameAddress < 0 || frameAddress >= model.Count) return;
         viewPort.ChangeHistory.ChangeCompleted();
         var page = tilePalette != null && item.Entry.FirstTile >= 0 && item.Entry.FirstTile < tilePalette.Length ? tilePalette[item.Entry.FirstTile] : 0;
         if (model.GetNextRun(frameAddress) is ISpriteRun && TryOpenImageEditor(frameAddress, page, item.Entry.TileCount)) return;
         viewPort.Goto.Execute(frameAddress);
         RequestTabChange?.Invoke(this, new TabChangeRequestedEventArgs(viewPort));
      }

      /// <summary>
      /// Open the image editor on a sprite. The editor is created by the main tab; if nothing is listening for new tabs, fall back to the main tab.
      /// </summary>
      private bool TryOpenImageEditor(int address, int palettePage, int preferredTileWidth) {
         bool opened = false;
         void Capture(object sender, TabChangeRequestedEventArgs e) {
            if (e.NewTab is ImageEditorViewModel) { RequestTabChange?.Invoke(this, e); opened = e.RequestAccepted; }
         }
         viewPort.RequestTabChange += Capture;
         try {
            viewPort.OpenImageEditorTab(address, 0, palettePage, preferredTileWidth);
         } finally {
            viewPort.RequestTabChange -= Capture;
         }
         return opened;
      }

      private void ExecuteGotoTable() {
         var blockset = CurrentBlockset();
         if (blockset == null || !animations.TryGetTable(blockset.Start, out var table, out _)) return;
         viewPort.Goto.Execute(table.Start);
         RequestTabChange?.Invoke(this, new TabChangeRequestedEventArgs(viewPort));
      }

      /// <summary>
      /// Import frames from an image: each frame is (tiles*8) pixels wide and 8 pixels tall; frames are stacked top to bottom (or side by side if the image is 8 pixels tall).
      /// Colors are matched to the palette the tiles use in this tileset.
      /// </summary>
      private void ExecuteImportFrames() {
         if (selectedEntry == null) return;
         var entry = selectedEntry.Entry;
         (short[] image, int width) loaded;
         try {
            loaded = fileSystem.LoadImage();
         } catch (Exception e) {
            OnError?.Invoke(this, "Could not load the image: " + e.Message);
            return;
         }
         if (loaded.image == null || loaded.width <= 0) return;
         int height = loaded.image.Length / loaded.width;
         int frameWidth = entry.TileCount * 8;
         int frames;
         bool horizontal;
         if (loaded.width == frameWidth && height % 8 == 0) { frames = height / 8; horizontal = false; } else if (height == 8 && loaded.width % frameWidth == 0) { frames = loaded.width / frameWidth; horizontal = true; } else {
            OnError?.Invoke(this, $"Expected an image {frameWidth} pixels wide with the frames stacked (8 pixels per frame), or 8 pixels tall with the frames side by side. This image is {loaded.width}x{height}.");
            return;
         }
         if (frames < 1 || frames > 64) { OnError?.Invoke(this, "Frame count must be between 1 and 64."); return; }
         var blockset = CurrentBlockset();
         if (blockset == null || !animations.TryGetTable(blockset.Start, out var table, out _)) return;
         if (frames != entry.FrameCount) animations.SetFrameCount(table, entry.Index, frames);
         entry = animations.ReadEntries(table, isSecondary, constants)[entry.Index];
         var palette = PaletteFor(entry.FirstTile);
         for (int f = 0; f < frames; f++) {
            var data = new byte[32 * entry.TileCount];
            for (int t = 0; t < entry.TileCount; t++) {
               for (int y = 0; y < 8; y++) {
                  for (int x = 0; x < 8; x++) {
                     int px = horizontal ? f * frameWidth + t * 8 + x : t * 8 + x;
                     int py = horizontal ? y : f * 8 + y;
                     var color = loaded.image[py * loaded.width + px];
                     int index = NearestColor(palette, color);
                     int offset = t * 32 + y * 4 + x / 2;
                     if (x % 2 == 0) data[offset] = (byte)((data[offset] & 0xF0) | index); else data[offset] = (byte)((data[offset] & 0x0F) | (index << 4));
                  }
               }
            }
            animations.WriteFrame(entry, f, data);
         }
         viewPort.ChangeHistory.ChangeCompleted();
         viewPort.Refresh();
         var selected = entry.Index;
         Reload();
         SelectedEntry = selected < Entries.Count ? Entries[selected] : null;
         OnMessage?.Invoke(this, $"Imported {frames} frame{(frames == 1 ? "" : "s")}.");
      }

      private void ExecuteExportFrames() {
         if (selectedEntry == null) return;
         var entry = selectedEntry.Entry;
         int frameWidth = entry.TileCount * 8;
         var image = new short[frameWidth * 8 * entry.FrameCount];
         var palette = PaletteFor(entry.FirstTile);
         for (int f = 0; f < entry.FrameCount; f++) {
            var data = animations.ReadFrame(entry, f);
            if (data == null) continue;
            for (int t = 0; t < entry.TileCount; t++) {
               for (int y = 0; y < 8; y++) {
                  for (int x = 0; x < 8; x++) {
                     var b = data[t * 32 + y * 4 + x / 2];
                     var index = x % 2 == 0 ? b & 0xF : b >> 4;
                     image[(f * 8 + y) * frameWidth + t * 8 + x] = palette[index];
                  }
               }
            }
         }
         try {
            fileSystem.SaveImage(image, frameWidth);
         } catch (Exception e) {
            OnError?.Invoke(this, "Could not save the image: " + e.Message);
         }
      }

      private static int NearestColor(IReadOnlyList<short> palette, short color) {
         int best = 0, bestDistance = int.MaxValue;
         int r = color & 31, g = (color >> 5) & 31, b = (color >> 10) & 31;
         for (int i = 0; i < palette.Count; i++) {
            int pr = palette[i] & 31, pg = (palette[i] >> 5) & 31, pb = (palette[i] >> 10) & 31;
            int distance = (r - pr) * (r - pr) + (g - pg) * (g - pg) + (b - pb) * (b - pb);
            if (distance < bestDistance) { bestDistance = distance; best = i; }
         }
         return best;
      }

      #endregion

      #region Doors

      public bool HasDoors => DoorAnimations.IsSupported(model);
      public ObservableCollection<DoorItem> Doors { get; } = new();
      public ObservableCollection<string> DoorSizes { get; } = new() { "1x1 (one block)", "1x2 (two blocks tall)", "2x2 left half", "2x2 right half" };
      public ObservableCollection<string> DoorSounds { get; } = new() { "normal", "sliding", "arena" };

      private DoorItem selectedDoor;
      public DoorItem SelectedDoor {
         get => selectedDoor;
         set {
            if (selectedDoor == value) return;
            if (selectedDoor != null) selectedDoor.Selected = false;
            selectedDoor = value;
            if (selectedDoor != null) selectedDoor.Selected = true;
            NotifyPropertyChanged();
            NotifyPropertyChanged(nameof(HasSelectedDoor));
            removeDoor?.RaiseCanExecuteChanged();
            importDoorFrames?.RaiseCanExecuteChanged();
            exportDoorFrames?.RaiseCanExecuteChanged();
         }
      }
      public bool HasSelectedDoor => selectedDoor != null;

      private int newDoorMetatile;
      /// <summary>The block (metatile) number the new door sits on. For a 1x2 door, the bottom block.</summary>
      public int NewDoorMetatile { get => newDoorMetatile; set { Set(ref newDoorMetatile, value.LimitToRange(0, 0x3FF)); NotifyPropertyChanged(nameof(NewDoorPreview)); } }
      private int newDoorSize = 1;
      public int NewDoorSize { get => newDoorSize; set => Set(ref newDoorSize, value.LimitToRange(0, 3)); }
      private int newDoorSound;
      public int NewDoorSound { get => newDoorSound; set => Set(ref newDoorSound, value.LimitToRange(0, 2)); }
      public IPixelViewModel NewDoorPreview => RenderBlock(newDoorMetatile);

      private StubCommand addDoor, removeDoor, importDoorFrames, exportDoorFrames, gotoDoorTable;
      public ICommand AddDoor => StubCommand(ref addDoor, ExecuteAddDoor, () => HasDoors && tiles != null);
      public ICommand RemoveDoor => StubCommand(ref removeDoor, ExecuteRemoveDoor, () => selectedDoor != null);
      public ICommand ImportDoorFrames => StubCommand(ref importDoorFrames, ExecuteImportDoorFrames, () => selectedDoor != null);
      public ICommand ExportDoorFrames => StubCommand(ref exportDoorFrames, ExecuteExportDoorFrames, () => selectedDoor != null);
      public ICommand GotoDoorTable => StubCommand(ref gotoDoorTable, () => { var table = doors.Table; if (table == null) return; viewPort.Goto.Execute(table.Start); RequestTabChange?.Invoke(this, new TabChangeRequestedEventArgs(viewPort)); }, () => HasDoors);

      private byte[][] allBlocks;          // every block of the map (primary + secondary), for metatile previews
      private int[][,] allTiles;           // every tile of the map (primary + secondary)

      private void LoadDoors(MiniBlocksetModel blockset) {
         Doors.Clear();
         allBlocks = null;
         allTiles = null;
         if (!HasDoors || blockset == null) return;
         foreach (var entry in doors.ReadEntries()) {
            if (entry.TilesetAddress != blockset.Start) continue;
            Doors.Add(new DoorItem(entry, this) { SpriteScale = spriteScale });
         }
         NotifyPropertyChanged(nameof(NewDoorPreview));
         addDoor?.RaiseCanExecuteChanged();
         gotoDoorTable?.RaiseCanExecuteChanged();
      }

      private void EnsureBlocks() {
         if (allBlocks != null || selectedMap == null) return;
         try {
            var all = AllMapsModel.Create(model);
            var layout = all[selectedMap.Group]?[selectedMap.Map]?.Layout;
            if (layout?.PrimaryBlockset == null || layout.SecondaryBlockset == null) return;
            var primary = layout.PrimaryBlockset.FullBlocksetModel;
            var secondary = layout.SecondaryBlockset.FullBlocksetModel;
            allTiles = BlockmapRun.ReadTiles(primary, secondary, constants?.PrimaryTiles ?? 512);
            allBlocks = BlockmapRun.ReadBlocks(primary.PrimaryBlocks, 1024 - primary.PrimaryBlocks, primary, secondary);
         } catch (Exception) {
            allBlocks = null;
            allTiles = null;
         }
      }

      /// <summary>Render one 16x16 block of the current map (for door previews).</summary>
      public IPixelViewModel RenderBlock(int metatile) {
         EnsureBlocks();
         if (allBlocks == null || allTiles == null || palettes == null || metatile < 0 || metatile >= allBlocks.Length) return new ReadonlyPixelViewModel(16, 16);
         try {
            var paletteList = palettes.Select(p => (IReadOnlyList<short>)(p ?? new short[16])).ToArray();
            return BlocksetModel.RenderBlock(metatile, allBlocks, null, allTiles, paletteList);
         } catch (Exception) {
            return new ReadonlyPixelViewModel(16, 16);
         }
      }

      /// <summary>Render one frame of a door using the tileset palettes the door's palette table names.</summary>
      public IPixelViewModel RenderDoorFrame(DoorEntry entry, int frame) {
         var data = doors.ReadTiles(entry);
         var paletteIndices = doors.ReadPaletteIndices(entry);
         int width = entry.WidthTiles * 8, height = entry.HeightTiles * 8;
         var pixels = new short[width * height];
         if (data == null) return new ReadonlyPixelViewModel(width, height, pixels);
         var tilesetPalettes = ReadTilesetPalettes(entry.TilesetAddress);
         for (int t = 0; t < entry.TilesPerFrame; t++) {
            var palette = tilesetPalettes[paletteIndices[t] & 15];
            int tx = (t % entry.WidthTiles) * 8, ty = (t / entry.WidthTiles) * 8;
            int offset = (frame * entry.TilesPerFrame + t) * 32;
            for (int y = 0; y < 8; y++) {
               for (int x = 0; x < 8; x++) {
                  var b = data[offset + y * 4 + x / 2];
                  var index = x % 2 == 0 ? b & 0xF : b >> 4;
                  pixels[(ty + y) * width + tx + x] = palette[index];
               }
            }
         }
         return new ReadonlyPixelViewModel(width, height, pixels);
      }

      private short[][] ReadTilesetPalettes(int tilesetAddress) {
         var result = new short[16][];
         var start = tilesetAddress >= 0 && tilesetAddress + 12 <= model.Count ? model.ReadPointer(tilesetAddress + 8) : -1;
         for (int p = 0; p < 16; p++) {
            result[p] = new short[16];
            if (start < 0 || start + 512 > model.Count) continue;
            for (int c = 0; c < 16; c++) result[p][c] = PaletteRun.FlipColorChannels((short)model.ReadMultiByteValue(start + p * 32 + c * 2, 2));
            // the secondary tileset's palette table only holds its own palettes (6..12): fall back to the map's merged palettes for the rest
            if (palettes != null && p < palettes.Length && palettes[p] != null && result[p].All(color => color == 0)) result[p] = palettes[p];
         }
         return result;
      }

      private void ExecuteAddDoor() {
         var blockset = CurrentBlockset();
         if (blockset == null) return;
         try {
            // first frame: whatever the block looks like now (its bottom layer), so the door starts out looking right
            var palette = tilePalette != null && tilePalette.Length > 0 ? tilePalette[Math.Min(tilePalette.Length - 1, firstTile)] : (isSecondary ? 6 : 0);
            var entry = doors.AddDoor(blockset.Start, newDoorMetatile, newDoorSize, newDoorSound, palette, null);
            viewPort.ChangeHistory.ChangeCompleted();
            viewPort.Refresh();
            Reload();
            SelectedDoor = Doors.FirstOrDefault(door => door.Entry.Index == entry.Index);
            OnMessage?.Invoke(this, $"Added a {entry.SizeName} door on block {entry.Metatile}. Its frames are blank: import a picture ({entry.WidthTiles * 8}x{entry.HeightTiles * 8 * DoorEntry.FrameCount}, the 3 opening stages stacked) or click a frame to draw it.");
         } catch (Exception e) {
            OnError?.Invoke(this, "Could not add the door: " + e.Message);
         }
      }

      private void ExecuteRemoveDoor() {
         if (selectedDoor == null) return;
         doors.RemoveDoor(selectedDoor.Entry.Index);
         viewPort.ChangeHistory.ChangeCompleted();
         viewPort.Refresh();
         Reload();
      }

      /// <summary>Open the door's 3 frames (stacked top to bottom) in the image editor, using the palette the door's tiles use.</summary>
      public void EditDoorFrame(DoorItem item, int frame) {
         if (item == null) return;
         var entry = item.Entry;
         if (entry.TilesAddress < 0 || entry.TilesAddress + entry.TilesLength > model.Count) return;
         doors.EnsureTilesFormat(entry);
         viewPort.ChangeHistory.ChangeCompleted();
         var indices = doors.ReadPaletteIndices(entry);
         var page = indices.Length > 0 ? indices[0] : 0;
         if (model.GetNextRun(entry.TilesAddress) is ISpriteRun && TryOpenImageEditor(entry.TilesAddress, page, -1)) return;
         viewPort.Goto.Execute(entry.TilesAddress);
         RequestTabChange?.Invoke(this, new TabChangeRequestedEventArgs(viewPort));
      }

      public void SetDoorSound(DoorItem item, int sound) {
         if (item == null) return;
         doors.SetSound(item.Entry, sound);
         viewPort.ChangeHistory.ChangeCompleted();
         item.Refresh(doors.ReadEntries()[item.Entry.Index], this);
      }

      public void SetDoorMetatile(DoorItem item, int metatile) {
         if (item == null) return;
         doors.SetMetatile(item.Entry, metatile);
         viewPort.ChangeHistory.ChangeCompleted();
         item.Refresh(doors.ReadEntries()[item.Entry.Index], this);
      }

      /// <summary>Import all 3 frames from one picture: (width x 8) by (height x 8 x 3) pixels, frames stacked top to bottom.</summary>
      private void ExecuteImportDoorFrames() {
         if (selectedDoor == null) return;
         var entry = selectedDoor.Entry;
         (short[] image, int width) loaded;
         try { loaded = fileSystem.LoadImage(); } catch (Exception e) { OnError?.Invoke(this, "Could not load the image: " + e.Message); return; }
         if (loaded.image == null || loaded.width <= 0) return;
         int height = loaded.image.Length / loaded.width;
         int expectedWidth = entry.WidthTiles * 8, expectedHeight = entry.HeightTiles * 8 * DoorEntry.FrameCount;
         if (loaded.width != expectedWidth || height != expectedHeight) {
            OnError?.Invoke(this, $"A {entry.SizeName} door's picture must be {expectedWidth}x{expectedHeight} pixels (the 3 frames stacked top to bottom). This image is {loaded.width}x{height}.");
            return;
         }
         var paletteIndices = doors.ReadPaletteIndices(entry);
         var tilesetPalettes = ReadTilesetPalettes(entry.TilesetAddress);
         var data = new byte[entry.TilesLength];
         for (int f = 0; f < DoorEntry.FrameCount; f++) {
            for (int t = 0; t < entry.TilesPerFrame; t++) {
               var palette = tilesetPalettes[paletteIndices[t] & 15];
               int tx = (t % entry.WidthTiles) * 8, ty = f * entry.HeightTiles * 8 + (t / entry.WidthTiles) * 8;
               int offset = (f * entry.TilesPerFrame + t) * 32;
               for (int y = 0; y < 8; y++) {
                  for (int x = 0; x < 8; x++) {
                     int colorIndex = NearestColor(palette, loaded.image[(ty + y) * loaded.width + tx + x]);
                     int o = offset + y * 4 + x / 2;
                     if (x % 2 == 0) data[o] = (byte)((data[o] & 0xF0) | colorIndex); else data[o] = (byte)((data[o] & 0x0F) | (colorIndex << 4));
                  }
               }
            }
         }
         doors.WriteTiles(entry, data);
         viewPort.ChangeHistory.ChangeCompleted();
         viewPort.Refresh();
         var index = entry.Index;
         Reload();
         SelectedDoor = Doors.FirstOrDefault(door => door.Entry.Index == index);
         OnMessage?.Invoke(this, "Imported the door's frames.");
      }

      private void ExecuteExportDoorFrames() {
         if (selectedDoor == null) return;
         var entry = selectedDoor.Entry;
         int width = entry.WidthTiles * 8, frameHeight = entry.HeightTiles * 8;
         var image = new short[width * frameHeight * DoorEntry.FrameCount];
         for (int f = 0; f < DoorEntry.FrameCount; f++) {
            var frame = RenderDoorFrame(entry, f);
            Array.Copy(frame.PixelData, 0, image, f * width * frameHeight, width * frameHeight);
         }
         try { fileSystem.SaveImage(image, width); } catch (Exception e) { OnError?.Invoke(this, "Could not save the image: " + e.Message); }
      }

      #endregion

      public void Reload() {
         Entries.Clear();
         tiles = null;
         rawTiles = null;
         var blockset = CurrentBlockset();
         if (constants == null) {
            Status = "This ROM's metadata doesn't say where the tileset animation engine keeps its data (tilesetanim.* constants), so animations can't be installed.";
            TilesetImage = new ReadonlyPixelViewModel(128, 256);
            return;
         }
         if (blockset == null || blockset.Start < 0 || blockset.Start >= model.Count) {
            Status = "Pick a map to edit its tileset animations.";
            TilesetImage = new ReadonlyPixelViewModel(128, 256);
            return;
         }
         try {
            var full = blockset.FullBlocksetModel;
            tiles = full.ReadTiles();
            TileCountInTileset = tiles?.Length ?? 0;
            rawTiles = ReadRawTiles(full, TileCountInTileset);
            LoadPalettes();
            GuessTilePalettes(full);
            TilesetImage = RenderTileset();
            TilesetDescription = $"{(isSecondary ? "Secondary" : "Primary")} tileset at {blockset.Start:X6}: {TileCountInTileset} tiles, tiles at {blockset.TilesetAddress:X6}";
            AnimationDescription = animations.DescribeCallback(blockset.Start);
            if (animations.TryGetTable(blockset.Start, out var table, out _)) {
               foreach (var entry in animations.ReadEntries(table, isSecondary, constants)) Entries.Add(new TilesetAnimationItem(entry, this) { SpriteScale = spriteScale });
            }
            FirstTile = Math.Min(firstTile, Math.Max(0, TileCountInTileset - 1));
            NotifyPropertyChanged(nameof(SelectionPreview));
            NotifyPropertyChanged(nameof(SelectionText));
            NotifyPropertyChanged(nameof(TileCountInTileset));
            LoadDoors(blockset);
            Status = $"{Entries.Count} custom animation{(Entries.Count == 1 ? "" : "s")} and {Doors.Count} door{(Doors.Count == 1 ? "" : "s")} on this tileset.";
         } catch (Exception e) {
            Status = "Could not read this tileset: " + e.Message;
            TilesetImage = new ReadonlyPixelViewModel(128, 256);
         }
         addAnimation?.RaiseCanExecuteChanged();
         gotoTable?.RaiseCanExecuteChanged();
      }

      private static byte[] ReadRawTiles(BlocksetModel blockset, int tileCount) {
         var data = new byte[tileCount * 32];
         var tiles = blockset.ReadTiles();
         for (int t = 0; t < tileCount; t++) {
            for (int y = 0; y < 8; y++) {
               for (int x = 0; x < 8; x += 2) {
                  data[t * 32 + y * 4 + x / 2] = (byte)((tiles[t][x, y] & 0xF) | ((tiles[t][x + 1, y] & 0xF) << 4));
               }
            }
         }
         return data;
      }

      private void LoadPalettes() {
         palettes = null;
         if (selectedMap == null) return;
         var all = AllMapsModel.Create(model);
         var layout = all[selectedMap.Group]?[selectedMap.Map]?.Layout;
         if (layout?.PrimaryBlockset == null || layout.SecondaryBlockset == null) return;
         palettes = BlockmapRun.ReadPalettes(layout.PrimaryBlockset.FullBlocksetModel, layout.SecondaryBlockset.FullBlocksetModel, model.IsFRLG() ? 7 : 6);
      }

      /// <summary>
      /// Tiles don't store which palette they use: the blocks do. Count which palette each tile is drawn with, and use the most common one.
      /// </summary>
      private void GuessTilePalettes(BlocksetModel blockset) {
         tilePalette = new int[Math.Max(1, TileCountInTileset)];
         var counts = new Dictionary<int, int[]>();
         var tileBase = isSecondary ? constants.PrimaryTiles : 0;
         try {
            var blocks = blockset.ReadBlocks(blockset.PrimaryBlocks);
            foreach (var block in blocks) {
               for (int i = 0; i < 8; i++) {
                  var value = block[i * 2] | (block[i * 2 + 1] << 8);
                  var tile = (value & 0x3FF) - tileBase;
                  var palette = value >> 12;
                  if (tile < 0 || tile >= tilePalette.Length) continue;
                  if (!counts.TryGetValue(tile, out var tally)) counts[tile] = tally = new int[16];
                  tally[palette]++;
               }
            }
         } catch (Exception) {
            // no block information: every tile gets the default palette
         }
         var defaultPalette = isSecondary ? (model.IsFRLG() ? 7 : 6) : 0;
         for (int t = 0; t < tilePalette.Length; t++) {
            tilePalette[t] = defaultPalette;
            if (!counts.TryGetValue(t, out var tally)) continue;
            int best = 0;
            for (int p = 1; p < 16; p++) if (tally[p] > tally[best]) best = p;
            if (tally[best] > 0) tilePalette[t] = best;
         }
      }

      public IReadOnlyList<short> PaletteFor(int tile) {
         var index = tilePalette != null && tile >= 0 && tile < tilePalette.Length ? tilePalette[tile] : 0;
         if (palettes != null && index < palettes.Length && palettes[index] != null) return palettes[index];
         return Enumerable.Range(0, 16).Select(i => (short)(i * 0x0421 * 2)).ToList(); // grayscale fallback
      }

      private IPixelViewModel RenderTileset() {
         int rows = (TileCountInTileset + TilesPerRow - 1) / TilesPerRow;
         var canvas = new CanvasPixelViewModel(TilesPerRow * 8, Math.Max(8, rows * 8)) { SpriteScale = spriteScale };
         var pixels = new short[canvas.PixelWidth * canvas.PixelHeight];
         for (int t = 0; t < TileCountInTileset; t++) {
            var palette = PaletteFor(t);
            int tx = (t % TilesPerRow) * 8, ty = (t / TilesPerRow) * 8;
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) pixels[(ty + y) * canvas.PixelWidth + tx + x] = palette[tiles[t][x, y] & 0xF];
         }
         canvas.Fill(pixels);
         return canvas;
      }

      /// <summary>
      /// Render 'count' tiles from raw 4bpp data as one row (used for the selection preview and each animation frame).
      /// </summary>
      public IPixelViewModel RenderTileRow(int firstTile, int count, byte[] data, int dataTileOffset = -1, double scale = 1) {
         count = Math.Max(1, count);
         var pixels = new short[count * 8 * 8];
         if (data != null) {
            var palette = PaletteFor(firstTile);
            for (int t = 0; t < count; t++) {
               int sourceTile = dataTileOffset < 0 ? firstTile + t : dataTileOffset + t;
               if ((sourceTile + 1) * 32 > data.Length) break;
               for (int y = 0; y < 8; y++) {
                  for (int x = 0; x < 8; x++) {
                     var b = data[sourceTile * 32 + y * 4 + x / 2];
                     var index = x % 2 == 0 ? b & 0xF : b >> 4;
                     pixels[y * count * 8 + t * 8 + x] = palette[index];
                  }
               }
            }
         }
         return new ReadonlyPixelViewModel(count * 8, 8, pixels) { SpriteScale = scale };
      }

      public IPixelViewModel RenderFrame(TilesetAnimationEntry entry, int frame) {
         var data = animations.ReadFrame(entry, frame);
         return RenderTileRow(entry.FirstTile, entry.TileCount, data, 0);
      }
   }

   public record MapOption(int Group, int Map, string Name) {
      public string Label => $"{Group}-{Map} {Name}";
      public override string ToString() => Label;
   }

   public class TilesetAnimationItem : ViewModelCore {
      private readonly TilesetAnimationTab tab;
      public TilesetAnimationEntry Entry { get; private set; }
      public ObservableCollection<TilesetAnimationFrame> Frames { get; } = new();

      public string Label => $"#{Entry.Index + 1}: tiles {Entry.FirstTile}-{Entry.FirstTile + Entry.TileCount - 1}, {Entry.FrameCount} frame{(Entry.FrameCount == 1 ? "" : "s")}";
      public string Details => $"Changes {TilesetAnimationTab.DescribeSpeed(Entry.Timer)}. Frames at {Entry.FramesAddress:X6}.";

      private IPixelViewModel baseTiles;
      /// <summary>What these tiles look like in the tileset itself (frame 0 of the animation replaces them in game).</summary>
      public IPixelViewModel BaseTiles { get => baseTiles; private set { baseTiles = value; NotifyPropertyChanged(); } }

      public int Speed { get => Entry.Timer; set { if (value != Entry.Timer) tab.SetEntrySpeed(this, value); } }
      public string SpeedText => TilesetAnimationTab.DescribeSpeed(Entry.Timer);
      public int FrameCount { get => Entry.FrameCount; set { if (value != Entry.FrameCount) tab.SetEntryFrameCount(this, value); } }

      private double spriteScale = 2;
      public double SpriteScale { get => spriteScale; set { Set(ref spriteScale, value); foreach (var frame in Frames) frame.SpriteScale = value; NotifyPropertyChanged(nameof(LiveFrame)); } }

      private bool selected;
      public bool Selected { get => selected; set => TryUpdate(ref selected, value); }

      private int liveIndex;
      /// <summary>The frame the game would be showing right now (the view advances this with Tick).</summary>
      public IPixelViewModel LiveFrame => Frames.Count == 0 ? BaseTiles : Frames[Math.Min(liveIndex, Frames.Count - 1)];
      public void Tick(int tick) {
         if (Frames.Count < 2) return;
         if ((tick & ((1 << Entry.Timer) - 1)) != 0) return;
         liveIndex = (liveIndex + 1) % Frames.Count;
         NotifyPropertyChanged(nameof(LiveFrame));
      }

      public TilesetAnimationItem(TilesetAnimationEntry entry, TilesetAnimationTab tab) {
         this.tab = tab;
         Refresh(entry, tab);
      }

      public void Refresh(TilesetAnimationEntry entry, TilesetAnimationTab tab) {
         Entry = entry;
         Frames.Clear();
         for (int f = 0; f < entry.FrameCount; f++) Frames.Add(new TilesetAnimationFrame(this, f, tab.RenderFrame(entry, f)) { SpriteScale = spriteScale });
         BaseTiles = tab.RenderTileRow(entry.FirstTile, entry.TileCount, null, scale: spriteScale);
         liveIndex = 0;
         NotifyPropertyChanged(nameof(LiveFrame));
         NotifyPropertyChanged(nameof(Label));
         NotifyPropertyChanged(nameof(Details));
         NotifyPropertyChanged(nameof(Speed));
         NotifyPropertyChanged(nameof(SpeedText));
         NotifyPropertyChanged(nameof(FrameCount));
      }

      public void EditFrame(TilesetAnimationFrame frame) => tab.EditFrame(this, frame.Index);
   }

   public class DoorItem : ViewModelCore {
      private readonly TilesetAnimationTab tab;
      public DoorEntry Entry { get; private set; }
      public ObservableCollection<TilesetAnimationFrame> Frames { get; } = new();
      public string Label => $"Door on block {Entry.Metatile} ({Entry.SizeName}, {Entry.SoundName} sound)";
      public string Details => $"Frames at {Entry.TilesAddress:X6}, palettes at {Entry.PalettesAddress:X6}.";

      private IPixelViewModel block;
      public IPixelViewModel Block { get => block; private set { block = value; NotifyPropertyChanged(); } }

      public int Sound { get => Entry.Sound; set { if (value != Entry.Sound) tab.SetDoorSound(this, value); } }
      public int Metatile { get => Entry.Metatile; set { if (value != Entry.Metatile) tab.SetDoorMetatile(this, value); } }

      private double spriteScale = 2;
      public double SpriteScale { get => spriteScale; set { Set(ref spriteScale, value); foreach (var frame in Frames) frame.SpriteScale = value; NotifyPropertyChanged(nameof(LiveFrame)); } }

      private bool selected;
      public bool Selected { get => selected; set => TryUpdate(ref selected, value); }

      // closed, open (4 game frames per step, like the game), stay open while the player walks through, close again
      private static readonly (int frame, int ticks)[] Timeline = { (0, 45), (1, 4), (2, 4), (2, 40), (1, 4), (0, 4) };
      private int timelineStep, stepTicks;
      /// <summary>The frame the game would be showing right now as the door opens and closes (the view advances this with Tick).</summary>
      public IPixelViewModel LiveFrame => Frames.Count == 0 ? Block : Frames[Math.Min(Timeline[timelineStep].frame, Frames.Count - 1)];
      public void Tick() {
         if (Frames.Count == 0) return;
         stepTicks++;
         if (stepTicks < Timeline[timelineStep].ticks) return;
         stepTicks = 0;
         timelineStep = (timelineStep + 1) % Timeline.Length;
         NotifyPropertyChanged(nameof(LiveFrame));
      }

      public DoorItem(DoorEntry entry, TilesetAnimationTab tab) {
         this.tab = tab;
         Refresh(entry, tab);
      }

      public void Refresh(DoorEntry entry, TilesetAnimationTab tab) {
         Entry = entry;
         Frames.Clear();
         for (int f = 0; f < DoorEntry.FrameCount; f++) Frames.Add(new TilesetAnimationFrame(null, f, tab.RenderDoorFrame(entry, f)) { SpriteScale = spriteScale, Door = this });
         Block = tab.RenderBlock(entry.Metatile);
         timelineStep = 0; stepTicks = 0;
         NotifyPropertyChanged(nameof(LiveFrame));
         NotifyPropertyChanged(nameof(Label));
         NotifyPropertyChanged(nameof(Details));
         NotifyPropertyChanged(nameof(Sound));
         NotifyPropertyChanged(nameof(Metatile));
      }

      public void EditFrame(TilesetAnimationFrame frame) => tab.EditDoorFrame(this, frame.Index);
   }

   public class TilesetAnimationFrame : ViewModelCore, IPixelViewModel {
      private readonly IPixelViewModel image;
      public TilesetAnimationItem Owner { get; }
      public DoorItem Door { get; init; }
      public int Index { get; }
      public string Label => $"Frame {Index + 1}";
      public short Transparent => image.Transparent;
      public int PixelWidth => image.PixelWidth;
      public int PixelHeight => image.PixelHeight;
      public short[] PixelData => image.PixelData;
      private double spriteScale = 2;
      public double SpriteScale { get => spriteScale; set => Set(ref spriteScale, value); }
      public TilesetAnimationFrame(TilesetAnimationItem owner, int index, IPixelViewModel image) => (Owner, Index, this.image) = (owner, index, image);
   }
}
