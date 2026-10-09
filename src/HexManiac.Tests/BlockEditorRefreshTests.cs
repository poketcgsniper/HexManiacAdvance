using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.ViewModels;
using HavenSoft.HexManiac.Core.ViewModels.Map;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   /// <summary>
   /// The block editor keeps the pictures of the tileset's tiles (the picker you choose tiles from, the tile you picked, the eight tiles of the block).
   /// When the tileset changes (a tile was drawn in the image editor, or an animated tile was stored in a blank spot of the tileset) they must follow.
   /// </summary>
   public class BlockEditorRefreshTests {
      private const int PickedTile = 5;
      private static readonly short Red = (short)(31 << 10), Green = (short)(31 << 5), Blue = 31, Yellow = (short)((31 << 10) | (31 << 5));

      private readonly PokemonModel model = new(new byte[0x100]);
      private readonly ChangeHistory<ModelDelta> history;

      public BlockEditorRefreshTests() {
         history = new ChangeHistory<ModelDelta>(change => change.Revert(model));
         model.SetList(new ModelDelta(), "MapAttributeBehaviors", "Normal", "Tall Grass", "Water");
      }

      /// <summary>1024 tiles of one colour each: tile 5 uses colour <paramref name="colorOfPickedTile"/>, every other tile colour 1.</summary>
      private static int[][,] Tiles(int colorOfPickedTile) {
         var tiles = new int[1024][,];
         for (int i = 0; i < tiles.Length; i++) {
            tiles[i] = new int[8, 8];
            var color = i == PickedTile ? colorOfPickedTile : 1;
            for (int x = 0; x < 8; x++) for (int y = 0; y < 8; y++) tiles[i][x, y] = color;
         }
         return tiles;
      }

      private static short[][] Palettes(short colorOfIndex3 = 0) {
         var palettes = new short[16][];
         for (int p = 0; p < palettes.Length; p++) palettes[p] = new short[] { 0, Blue, Green, Red, Yellow, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
         if (colorOfIndex3 != 0) palettes[0][3] = colorOfIndex3;
         return palettes;
      }

      /// <summary>Four blocks that show the picked tile (palette 0) in all eight tile positions.</summary>
      private static byte[][] Blocks() => Enumerable.Range(0, 4).Select(_ => new byte[] { PickedTile, 0, PickedTile, 0, PickedTile, 0, PickedTile, 0, PickedTile, 0, PickedTile, 0, PickedTile, 0, PickedTile, 0 }).ToArray();

      private BlockEditor Create(int[][,] tiles, short[][] palettes) {
         var attributes = Enumerable.Range(0, 4).Select(_ => new byte[2]).ToArray();
         var editor = new BlockEditor(history, model, new MapTutorialsViewModel(), palettes, tiles, Blocks(), attributes);
         editor.BlockIndex = 1; // the pictures of a block are drawn when a block is chosen
         return editor;
      }

      /// <summary>The colour the picker shows for the first pixel of a tile.</summary>
      private static short PickerColor(BlockEditor editor, int tile) {
         var picture = editor.TileRender;
         return picture.PixelData[(tile / 16) * 8 * picture.PixelWidth + (tile % 16) * 8];
      }

      [Fact]
      public void TilePicker_ShowsTheTilesWhenOpened() {
         var editor = Create(Tiles(2), Palettes());

         editor.ShowTiles = true;

         Assert.Equal(Green, PickerColor(editor, PickedTile));
         Assert.Equal(Blue, PickerColor(editor, PickedTile + 1));
      }

      [Fact]
      public void TilePicker_FollowsTilesThatChangedAfterItWasDrawn() {
         var editor = Create(Tiles(2), Palettes());
         editor.ShowTiles = true;
         var old = editor.TileRender;
         var changes = new List<string>();
         editor.PropertyChanged += (sender, e) => changes.Add(e.PropertyName);

         editor.RefreshTileCache(Tiles(3)); // tile 5 was drawn on

         Assert.NotSame(old, editor.TileRender);
         Assert.Equal(Red, PickerColor(editor, PickedTile));
         Assert.Equal(Blue, PickerColor(editor, PickedTile + 1));
         Assert.Contains(nameof(BlockEditor.TileRender), changes);
      }

      [Fact]
      public void TilePicker_IsNotDrawnAgainForTheSameTiles() {
         var editor = Create(Tiles(2), Palettes());
         editor.ShowTiles = true;
         var old = editor.TileRender;
         var changes = new List<string>();
         editor.PropertyChanged += (sender, e) => changes.Add(e.PropertyName);

         editor.RefreshTileCache(Tiles(2)); // a copy that says the same thing: the editor refreshes this way every time the tab is shown
         editor.RefreshPaletteCache(Palettes());

         Assert.Same(old, editor.TileRender);
         Assert.DoesNotContain(nameof(BlockEditor.TileRender), changes);
      }

      [Fact]
      public void TilePicker_NotShownYet_UsesTheNewTilesWhenItIs() {
         var editor = Create(Tiles(2), Palettes());

         editor.RefreshTileCache(Tiles(4));
         Assert.Null(editor.TileRender); // nothing was needed yet, nothing was drawn

         editor.ShowTiles = true;
         Assert.Equal(Yellow, PickerColor(editor, PickedTile));
      }

      [Fact]
      public void TilePicker_FollowsPalettesThatChanged() {
         var editor = Create(Tiles(3), Palettes());
         editor.ShowTiles = true;
         Assert.Equal(Red, PickerColor(editor, PickedTile));

         editor.RefreshPaletteCache(Palettes(colorOfIndex3: Green));

         Assert.Equal(Green, PickerColor(editor, PickedTile));
      }

      [Fact]
      public void PickedTile_FollowsTilesThatChanged() {
         var editor = Create(Tiles(2), Palettes());
         editor.ShowTiles = true;
         editor.TileSelectionX = PickedTile * 24; // 24 pixels per tile in the picker
         Assert.Equal(Green, editor.DrawTileRender.PixelData[0]);

         editor.RefreshTileCache(Tiles(3));

         Assert.Equal(Red, editor.DrawTileRender.PixelData[0]);
      }

      [Fact]
      public void BlockPictures_FollowTilesThatChanged() {
         var editor = Create(Tiles(2), Palettes());
         Assert.Equal(Green, editor.LeftTopFront.PixelData[0]);
         Assert.Equal(Green, editor.RightBottomBack.PixelData[0]);
         var changes = new List<string>();
         editor.PropertyChanged += (sender, e) => changes.Add(e.PropertyName);

         editor.RefreshTileCache(Tiles(3));

         Assert.Equal(Red, editor.LeftTopFront.PixelData[0]);
         Assert.Equal(Red, editor.RightBottomBack.PixelData[0]);
         Assert.Contains(nameof(BlockEditor.LeftTopFront), changes);
      }
   }
}
