using HavenSoft.HexManiac.Core;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.Models.Runs.Sprites;
using HavenSoft.HexManiac.Core.ViewModels;
using HavenSoft.HexManiac.Core.ViewModels.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   /// <summary>
   /// Writing the picture that a sprite already shows must leave the ROM alone.
   /// HexManiac's LZ compressor is not as good as the one most tools use (it never starts a match on an odd byte),
   /// so recompressing an unchanged sprite used to make it bigger, move it to free space, and leave the old copy behind as 0xFF.
   /// A palette shared by many sprites also had its unused colors blacked out and its colors reshuffled by an import of an unchanged picture.
   /// </summary>
   public class ImageIdentityImportTests {
      private const int SpriteA = 0x100, SpriteB = 0x200, Palette = 0x400, LzPalette = 0x500;

      private byte[] data;
      private PokemonModel model;
      private ViewPort viewPort;
      private byte[] rawA, rawB;

      /// <summary>
      /// A compressor that is allowed to start matches anywhere and use any offset (like the common GBA tools).
      /// For data that repeats every 3 bytes it beats the HexManiac compressor.
      /// </summary>
      private static byte[] NaiveLz(byte[] raw) {
         var tokens = new List<(bool compressed, byte b1, byte b2)>();
         for (int i = 0; i < raw.Length;) {
            int bestLength = 0, bestOffset = 0;
            for (int offset = 2; offset <= 0x1000 && offset <= i; offset++) {
               int length = 0;
               while (i + length < raw.Length && length < 18 && raw[i + length - offset] == raw[i + length]) length++;
               if (length > bestLength) (bestLength, bestOffset) = (length, offset);
            }
            if (bestLength >= 3) {
               tokens.Add((true, (byte)(((bestLength - 3) << 4) | ((bestOffset - 1) >> 8)), (byte)(bestOffset - 1)));
               i += bestLength;
            } else {
               tokens.Add((false, raw[i], 0));
               i++;
            }
         }
         var result = new List<byte> { 0x10, (byte)raw.Length, (byte)(raw.Length >> 8), (byte)(raw.Length >> 16) };
         for (int t = 0; t < tokens.Count; t += 8) {
            byte flags = 0;
            for (int j = 0; j < 8 && t + j < tokens.Count; j++) if (tokens[t + j].compressed) flags |= (byte)(0x80 >> j);
            result.Add(flags);
            for (int j = 0; j < 8 && t + j < tokens.Count; j++) {
               result.Add(tokens[t + j].b1);
               if (tokens[t + j].compressed) result.Add(tokens[t + j].b2);
            }
         }
         return result.ToArray();
      }

      private static byte[] Repeating(int length, params byte[] pattern) => length.Range().Select(i => pattern[i % pattern.Length]).ToArray();

      /// <summary>Palette color i the way the ROM stores it: red in the low bits.</summary>
      private static short Stored(int i) => (short)(i | ((15 - i) << 5) | ((i * 2) << 10));

      /// <summary>Palette color i the way the editor shows it: red in the high bits.</summary>
      private static short Shown(int i) => (short)((i << 10) | ((15 - i) << 5) | (i * 2));

      /// <summary>
      /// spriteCount sprites (2x2 tiles, 4 bits per pixel, LZ compressed by a tool better than HexManiac's) in a table next to the palette they share.
      /// Only palette colors 1-6 are used by the sprites, 0 and 7-15 are not used by anything but still hold colors.
      /// </summary>
      private void Setup(int spriteCount, bool useLastColor = false) {
         data = 0x1000.Range().Select(i => (byte)0xFF).ToArray();
         rawA = Repeating(128, 0x12, 0x34, 0x56);
         rawB = useLastColor ? Repeating(128, 0x2F, 0x43, 0xF5) : Repeating(128, 0x21, 0x43, 0x65);
         var compressedA = NaiveLz(rawA);
         var compressedB = NaiveLz(rawB);
         Array.Copy(compressedA, 0, data, SpriteA, compressedA.Length);
         Array.Copy(compressedB, 0, data, SpriteB, compressedB.Length);
         for (int i = 0; i < 16; i++) {
            data[Palette + i * 2] = (byte)Stored(i);
            data[Palette + i * 2 + 1] = (byte)(Stored(i) >> 8);
         }

         model = new PokemonModel(data, null, BaseViewModelTestClass.Singletons);
         var delta = new ModelDelta();
         model.WritePointer(delta, 0x00, SpriteA);
         model.WritePointer(delta, 0x04, Palette);
         model.WritePointer(delta, 0x08, SpriteB);
         model.WritePointer(delta, 0x0C, Palette);
         using (ModelCacheScope.CreateScope(model)) {
            PokemonModel.ApplyAnchor(model, new ModelDelta(), 0, $"^table1[sprite<`lzs4x2x2|table1`> pal<`ucp4`>]{spriteCount}");
         }
         viewPort = new ViewPort(string.Empty, model, InstantDispatch.Instance, BaseViewModelTestClass.Singletons) { Width = 16, Height = 16 };
      }

      /// <summary>Export what the tool shows, then import that file again.</summary>
      private void ExportThenImport(int spriteAddress, bool asIndexed, int importChoice) {
         var tool = viewPort.Tools.SpriteTool;
         viewPort.Tools.SelectedTool = tool;
         tool.SpriteAddress = spriteAddress;
         int[,] indexed = null;
         IReadOnlyList<short> indexedPalette = null;
         short[] rgb = null;
         var width = 0;
         var fs = new StubFileSystem {
            SaveIndexedImage = (sprite, pal, name) => (indexed, indexedPalette) = (sprite, pal),
            SaveImage = (image, w, name) => (rgb, width) = (image, w),
            ShowOptions = (title, prompt, details, options) => importChoice,
            LoadImage = name => (rgb, width),
            TryLoadIndexImage = (ref string name, out int[,] image, out IReadOnlyList<short> pal) => {
               name = "image.png";
               (image, pal) = (indexed, indexedPalette);
               return asIndexed;
            },
         };
         tool.ExportPair.Execute(fs);
         if (!asIndexed) {
            // the same picture as a file with no palette
            width = indexed.GetLength(0);
            rgb = indexed.Length.Range().Select(i => indexedPalette[indexed[i % width, i / width]]).ToArray();
         }
         tool.ImportPair.Execute(fs);
      }

      [Fact]
      public void CompressorPremise_HexManiacCompressesWorseThanTheTestData() {
         Setup(1);
         var stored = model.GetNextRun(SpriteA).Length;
         Assert.True(LZRun.Compress(rawA, 0, rawA.Length).Count > stored, "the other tests need a sprite that HexManiac cannot store as small as it is now");
      }

      [Fact]
      public void LzSprite_SetPixelsToItsCurrentPixels_NothingMovesOrChanges() {
         Setup(1);
         var run = (LzSpriteRun)model.GetNextRun(SpriteA);
         var pixels = run.GetPixels(model, 0, -1);
         var before = model.RawData.ToArray();

         var result = run.SetPixels(model, new ModelDelta(), 0, pixels);

         Assert.Equal(SpriteA, result.Start);
         Assert.Equal(SpriteA, model.ReadPointer(0x00));
         Assert.Equal(before, model.RawData);
      }

      [Fact]
      public void LzSprite_SetPixelsToDifferentPixels_StillChangesTheData() {
         Setup(1);
         var run = (LzSpriteRun)model.GetNextRun(SpriteA);
         var pixels = run.GetPixels(model, 0, -1);
         pixels[0, 0] = (pixels[0, 0] + 1) % 16;

         var result = (LzSpriteRun)run.SetPixels(model, new ModelDelta(), 0, pixels);

         var decompressed = LZRun.Decompress(model, result.Start);
         Assert.Equal((rawA[0] & 0xF0) | ((rawA[0] + 1) & 0x0F), decompressed[0]);
         Assert.Equal(result.Start, model.ReadPointer(0x00));
      }

      [Fact]
      public void LzPalette_SetPaletteToItsCurrentColors_NothingMovesOrChanges() {
         // 32 bytes that repeat every 3 bytes: the other compressor stores them in fewer bytes than HexManiac does
         var paletteBytes = Repeating(32, 0x11, 0x22, 0x03);
         var compressed = NaiveLz(paletteBytes);
         Assert.True(LZRun.Compress(paletteBytes, 0, 32).Count > compressed.Length);
         Setup(1);
         Array.Copy(compressed, 0, data, LzPalette, compressed.Length);
         viewPort.Edit($"@{LzPalette:X} ^lzpal`lzp4` ");
         var run = (LzPaletteRun)model.GetNextRun(LzPalette);
         var colors = run.GetPalette(model, 0);
         var before = model.RawData.ToArray();

         var result = run.SetPalette(model, new ModelDelta(), 0, colors);

         Assert.Equal(LzPalette, result.Start);
         Assert.Equal(before, model.RawData);
      }

      [Fact]
      public void SingleSprite_ImportItsOwnIndexedExport_NothingMovesOrChanges() {
         Setup(1);
         var before = model.RawData.ToArray();

         ExportThenImport(SpriteA, asIndexed: true, importChoice: 0);

         Assert.Equal(SpriteA, viewPort.Tools.SpriteTool.SpriteAddress);
         Assert.Equal(before, model.RawData);
      }

      [Fact]
      public void SingleSprite_ImportItsOwnRgbExport_NothingMovesOrChanges() {
         Setup(1);
         var before = model.RawData.ToArray();

         ExportThenImport(SpriteA, asIndexed: false, importChoice: 0);

         Assert.Equal(SpriteA, viewPort.Tools.SpriteTool.SpriteAddress);
         Assert.Equal(before, model.RawData);
      }

      [Theory]
      [InlineData(0)]
      [InlineData(1)]
      [InlineData(2)]
      public void SharedPalette_ImportOwnIndexedExport_NothingMovesOrChanges(int importChoice) {
         Setup(2); // smart, greedy, cautious
         var before = model.RawData.ToArray();

         ExportThenImport(SpriteB, asIndexed: true, importChoice);

         Assert.Equal(SpriteB, viewPort.Tools.SpriteTool.SpriteAddress);
         Assert.Equal(before, model.RawData);
      }

      [Theory]
      [InlineData(0)]
      [InlineData(1)]
      [InlineData(2)]
      public void SharedPalette_ImportOwnRgbExport_NothingMovesOrChanges(int importChoice) {
         Setup(2);
         var before = model.RawData.ToArray();

         ExportThenImport(SpriteB, asIndexed: false, importChoice);

         Assert.Equal(SpriteB, viewPort.Tools.SpriteTool.SpriteAddress);
         Assert.Equal(before, model.RawData);
      }

      [Theory]
      [InlineData(0)]
      [InlineData(1)]
      [InlineData(2)]
      public void SharedPalette_SpriteUsesLastPaletteColor_ImportOwnIndexedExport_NothingMovesOrChanges(int importChoice) {
         Setup(2, useLastColor: true);
         var before = model.RawData.ToArray();

         ExportThenImport(SpriteB, asIndexed: true, importChoice);

         Assert.Equal(SpriteB, viewPort.Tools.SpriteTool.SpriteAddress);
         Assert.Equal(before, model.RawData);
      }

      [Theory]
      [InlineData(0)]
      [InlineData(2)]
      public void SharedPalette_ImportIndexedPictureThatUsesTheLastPaletteColor_ColorSurvives(int importChoice) {
         Setup(2);
         var tool = viewPort.Tools.SpriteTool;
         viewPort.Tools.SelectedTool = tool;
         tool.SpriteAddress = SpriteB;
         var palette = 16.Range().Select(i => Shown(i)).ToArray();
         var indexed = new int[tool.PixelWidth, tool.PixelHeight];
         for (int y = 0; y < tool.PixelHeight; y++) for (int x = 0; x < tool.PixelWidth; x++) indexed[x, y] = 1 + (x + y) % 6;
         indexed[3, 4] = 15; // the last color of the palette
         var fs = new StubFileSystem {
            TryLoadIndexImage = (ref string name, out int[,] image, out IReadOnlyList<short> pal) => { name = "image.png"; (image, pal) = (indexed, palette); return true; },
            ShowOptions = (title, prompt, details, options) => importChoice,
         };

         tool.ImportPair.Execute(fs);

         Assert.Equal(Shown(15), tool.PixelData[4 * tool.PixelWidth + 3]);
      }

      [Fact]
      public void SharedPalette_ImportChangedPicture_OnlyThatSpriteChanges() {
         Setup(2);
         var tool = viewPort.Tools.SpriteTool;
         viewPort.Tools.SelectedTool = tool;
         tool.SpriteAddress = SpriteB;
         var otherBefore = LZRun.Decompress(model, SpriteA);
         var changed = tool.PixelData.ToArray();
         changed[0] = Shown(5); // a color that the palette already has, but not the one in the corner now
         var fs = new StubFileSystem {
            LoadImage = name => (changed, tool.PixelWidth),
            TryLoadIndexImage = (ref string name, out int[,] image, out IReadOnlyList<short> pal) => { name = "image.png"; (image, pal) = (null, null); return false; },
            ShowOptions = (title, prompt, details, options) => (int)ImportType.Cautious,
         };

         tool.ImportPair.Execute(fs);

         Assert.Equal(otherBefore, LZRun.Decompress(model, SpriteA));
         Assert.Equal(Shown(5), tool.PixelData[0]);
      }
   }
}
