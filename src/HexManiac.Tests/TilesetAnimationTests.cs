using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Map;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.Models.Runs.Sprites;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
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

      [Fact]
      public void AddDoor_GoesInFrontOfTheTerminator() {
         // a doors table with one real door and the all-zero terminator the game stops at
         var tableStart = 0x600;
         Model.WriteMultiByteValue(tableStart, 2, Token, 33);
         Model.WritePointer(Token, tableStart + 4, TilesetStart);
         Model[tableStart + 8] = 0; Model[tableStart + 9] = 1;
         Model.WritePointer(Token, tableStart + 12, 0xC00);
         Model.WritePointer(Token, tableStart + 16, 0xD00);
         for (int i = 20; i < 40; i++) Model[tableStart + i] = 0;
         ViewPort.Edit($"@{tableStart:X6} ^{DoorAnimations.TableName}[metatile: unused: tileset<> sound. size. unused: tiles<`ucs4x2x12`> palettes<>]2 ");
         var doors = new DoorAnimations(Model, () => Token);
         Assert.True(DoorAnimations.IsSupported(Model));
         Assert.Equal(1, doors.TerminatorIndex(doors.Table));

         var added = doors.AddDoor(TilesetStart, 34, 1, 1, 2);

         var table = doors.Table;
         Assert.Equal(3, table.ElementCount);
         Assert.Equal(1, added.Index);
         Assert.Equal(34, added.Metatile);
         Assert.Equal(2, doors.TerminatorIndex(table));
         Assert.Equal(Pointer.NULL, Model.ReadPointer(table.Start + table.ElementLength * 2 + 12));
         Assert.Equal(33, Model.ReadMultiByteValue(table.Start, 2));
         Assert.Equal(0xC00, Model.ReadPointer(table.Start + 12));
         Assert.IsType<SpriteRun>(Model.GetNextRun(added.TilesAddress));
         Assert.Equal(DoorAnimations.PaletteHint, ((ISpriteRun)Model.GetNextRun(added.TilesAddress)).SpriteFormat.PaletteHint);

         doors.RemoveDoor(1);

         table = doors.Table;
         Assert.Equal(2, table.ElementCount);
         Assert.Equal(1, doors.TerminatorIndex(table));
      }

      [Fact]
      public void BuiltInEntries_AreReadFromTheBuildTable() {
         // two rows of: tileset<> frames<> frameCount: firstTile: tileCount. timerShift. phase. padding. name""32
         const int table = 0x700, secondTileset = 0x40;
         for (int row = 0; row < 2; row++) {
            var start = table + row * 48;
            Model.WritePointer(Token, start, row == 0 ? TilesetStart : secondTileset);
            Model.WritePointer(Token, start + 4, 0x300 + row * 0x20);
            Model.WriteMultiByteValue(start + 8, 2, Token, 2);
            Model.WriteMultiByteValue(start + 10, 2, Token, row == 0 ? 432 : 512 + 96);
            Model[start + 12] = 2;
            Model[start + 13] = (byte)(row == 0 ? 3 : 4);
            Model[start + 14] = (byte)row;
            var name = row == 0 ? "Water" : "Steam";
            var text = Model.TextConverter.Convert(name, out var _);
            for (int i = 0; i < 32; i++) Model[start + 16 + i] = i < text.Count && text[i] != 0xFF ? text[i] : (byte)0xFF;
            Model.WritePointer(Token, 0x300 + row * 0x20, 0x400 + row * 0x80);
            Model.WritePointer(Token, 0x304 + row * 0x20, 0x440 + row * 0x80);
         }
         for (int i = 0; i < 64; i++) Model[0x440 + i] = (byte)(i + 1);
         ViewPort.Edit($"@{table:X6} ^{TilesetAnimations.BuiltInTableName}[tileset<> frames<> frameCount: firstTile: tileCount. timerShift. phase. padding. name\"\"32]2 ");
         var animations = CreateAnimations();
         TilesetAnimationConstants.TryRead(Model, out var constants);

         var primary = animations.ReadBuiltInEntries(TilesetStart, false, constants);
         var secondary = animations.ReadBuiltInEntries(secondTileset, true, constants);

         Assert.True(animations.HasBuiltInTable);
         var water = Assert.Single(primary);
         Assert.True(water.IsBuiltIn);
         Assert.Equal("Water", water.Name);
         Assert.Equal(432, water.FirstTile);
         Assert.Equal(2, water.TileCount);
         Assert.Equal(2, water.FrameCount);
         Assert.Equal(3, water.Timer);
         Assert.Equal(0, water.Phase);
         Assert.Equal(Enumerable.Range(0, 64).Select(i => (byte)(i + 1)), animations.ReadFrame(water, 1));
         var steam = Assert.Single(secondary);
         Assert.Equal("Steam", steam.Name);
         Assert.Equal(96, steam.FirstTile);      // secondary tilesets count from the end of the primary tiles
         Assert.Equal(1, steam.Phase);
      }
   }
}
