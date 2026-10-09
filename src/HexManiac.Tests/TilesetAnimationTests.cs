using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Map;
using HavenSoft.HexManiac.Core.Models.Runs;
using System.Linq;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class TilesetAnimationTests : BaseViewModelTestClass {
      private const int TilesetStart = 0x20;

      public TilesetAnimationTests() : base(0x1000) {
         SetFullModel(0xFF);
         // a fake tileset header: isCompressed, isSecondary, 2 flags, tiles, palettes, blocks, attributes, animation (null)
         Model[TilesetStart] = 1;
         Model[TilesetStart + 1] = 0;
         Model.WritePointer(Token, TilesetStart + 4, 0x800);
         Model.WritePointer(Token, TilesetStart + 8, 0x900);
         Model.WritePointer(Token, TilesetStart + 12, 0xA00);
         Model.WritePointer(Token, TilesetStart + 16, 0xB00);
         Model.WriteValue(Token, TilesetStart + 20, 0);
         foreach (var (name, value) in new[] {
            ("buffer", 0x02037000), ("buffersize", 0x03001000), ("primary.counter", 0x03001002), ("primary.max", 0x03001004), ("primary.callback", 0x03001008),
            ("secondary.counter", 0x0300100C), ("secondary.max", 0x0300100E), ("secondary.callback", 0x03001010), ("primarytiles", 512),
         }) Model.SetUnmappedConstant(Token, TilesetAnimationConstants.Prefix + name, value);
         Model.FreeSpaceStart = 0x100;
      }

      private TilesetAnimations CreateAnimations() => new(Model, () => Token, ViewPort.Tools.CodeTool.Parser);

      [Fact]
      public void Constants_AreReadFromMetadata() {
         Assert.True(TilesetAnimationConstants.TryRead(Model, out var constants));
         Assert.Equal(0x02037000, constants.Buffer);
         Assert.Equal(512, constants.PrimaryTiles);
         Assert.True(TilesetAnimations.IsSupported(Model));
      }

      [Fact]
      public void EnsureTable_InstallsCodeAndPointsTilesetAtIt() {
         var animations = CreateAnimations();
         TilesetAnimationConstants.TryRead(Model, out var constants);

         var table = animations.EnsureTable(TilesetStart, false, constants, out var baseName);

         Assert.Equal(0, table.ElementCount);
         Assert.StartsWith(TilesetAnimations.AnchorPrefix, baseName);
         var callback = animations.ReadCallback(TilesetStart);
         Assert.Equal(1, callback & 1); // thumb bit
         Assert.Equal(baseName + ".init", Model.GetAnchorFromAddress(-1, callback));
         Assert.True(animations.TryGetTable(TilesetStart, out var again, out _));
         Assert.Equal(table.Start, again.Start);
         Assert.Equal(0xB5, Model[(callback & ~1) + 1]); // push {.., lr}
      }

      [Fact]
      public void AddEntry_CreatesFramesFromCurrentTiles() {
         var animations = CreateAnimations();
         TilesetAnimationConstants.TryRead(Model, out var constants);
         var table = animations.EnsureTable(TilesetStart, false, constants, out _);
         var tiles = Enumerable.Range(0, 32 * 16).Select(i => (byte)(i & 0x77)).ToArray();

         var entry = animations.AddEntry(table, 4, 2, 3, 4, false, constants, tiles);

         Assert.Equal(4, entry.FirstTile);
         Assert.Equal(2, entry.TileCount);
         Assert.Equal(3, entry.FrameCount);
         Assert.Equal(4, entry.Timer);
         for (int f = 0; f < 3; f++) {
            var frame = animations.ReadFrame(entry, f);
            Assert.Equal(tiles.Skip(4 * 32).Take(64), frame);
         }
         // the frames don't overlap each other or the frame table
         var frameAddresses = Enumerable.Range(0, 3).Select(f => Model.ReadPointer(entry.FramesAddress + 4 * f)).ToList();
         Assert.Equal(3, frameAddresses.Distinct().Count());
         Assert.All(frameAddresses, address => Assert.True(address >= entry.FramesAddress + 12 || address + 64 <= entry.FramesAddress));
      }

      [Fact]
      public void RemoveEntry_ShiftsLaterEntries() {
         var animations = CreateAnimations();
         TilesetAnimationConstants.TryRead(Model, out var constants);
         var table = animations.EnsureTable(TilesetStart, false, constants, out _);
         animations.AddEntry(table, 1, 1, 2, 1, false, constants, null);
         table = animations.TryGetTable(TilesetStart, out var t2, out _) ? t2 : table;
         animations.AddEntry(table, 7, 3, 2, 2, false, constants, null);
         animations.TryGetTable(TilesetStart, out table, out _);
         Assert.Equal(2, table.ElementCount);

         animations.RemoveEntry(table, 0);

         animations.TryGetTable(TilesetStart, out table, out _);
         var entries = animations.ReadEntries(table, false, constants);
         Assert.Single(entries);
         Assert.Equal(7, entries[0].FirstTile);
         Assert.Equal(3, entries[0].TileCount);
      }

      [Fact]
      public void SecondaryTileset_OffsetsTilesByPrimaryCount() {
         var animations = CreateAnimations();
         TilesetAnimationConstants.TryRead(Model, out var constants);
         Model[TilesetStart + 1] = 1;
         var table = animations.EnsureTable(TilesetStart, true, constants, out _);

         var entry = animations.AddEntry(table, 5, 1, 2, 0, true, constants, null);

         Assert.Equal(5, entry.FirstTile);
         Assert.Equal(512 + 5, Model.ReadMultiByteValue(entry.EntryAddress + 8, 4));
      }
   }
}
