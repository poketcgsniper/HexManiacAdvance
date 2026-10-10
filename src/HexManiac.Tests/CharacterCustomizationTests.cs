using HavenSoft.HexManiac.Core;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels;
using HavenSoft.HexManiac.Core.ViewModels.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   /// <summary>
   /// A small ROM with the character customization tables the way the CUBE metadata describes them (anchors by name, never by address in the product code):
   /// 4 skin tones (row 0 = Original), 3 clothes colours, 6 roles, a counter byte for each, and pointers to the tables like the game's code has.
   /// The skin table is followed directly by the clothes table, so it can't grow in place; the clothes table has free space behind it.
   /// </summary>
   public abstract class CharacterCustomizationTestBase : BaseViewModelTestClass {
      protected const int SkinTable = 0x100, ClothesTable = 0x160, RoleTable = 0x200;
      protected const int SkinCount = 0x250, ClothesCount = 0x251, RoleCount = 0x252;
      protected const int PoolSkin = 0x260, PoolClothes = 0x264, PoolRoles = 0x268;
      protected const int RowLength = 24;
      private const string ColorRowFormat = "[name\"\"16 main:|c shadow:|c highlight:|c unused:]";
      private const string RoleFormat = "[gender.charcustomgenders context.charcustomcontexts skinMain. skinShadow. skinHighlight. skinOutline. clothesMain. clothesShadow. clothesHighlight. unused. unused. unused.]";

      protected static ushort Gba(int red, int green, int blue) => GbaColor.Pack(red, green, blue);

      protected CharacterCustomizationTestBase() : base(0x10000) {
         SetFullModel(0xFF);
         Model.SetList(new ModelDelta(), "charcustomgenders", "Male (Brendan)", "Female (May)");
         Model.SetList(new ModelDelta(), "charcustomcontexts", "Overworld", "Trainer pic", "Reflection");

         ViewPort.Edit($"@{SkinTable:X} ^{CharacterCustomization.SkinTonesAnchor}{ColorRowFormat}4 ");
         ViewPort.Edit($"@{ClothesTable:X} ^{CharacterCustomization.ClothesAnchor}{ColorRowFormat}3 ");
         ViewPort.Edit($"@{RoleTable:X} ^{CharacterCustomization.RolesAnchor}{RoleFormat}6 ");
         ViewPort.Edit($"@{SkinCount:X} ^{CharacterCustomization.SkinToneCountAnchor}[count.]1 ");
         ViewPort.Edit($"@{ClothesCount:X} ^{CharacterCustomization.ClothesCountAnchor}[count.]1 ");
         ViewPort.Edit($"@{RoleCount:X} ^{CharacterCustomization.RoleCountAnchor}[count.]1 ");

         WriteRow(SkinTable, 0, "Original");
         WriteRow(SkinTable, 1, "Fair", Gba(31, 28, 25), Gba(26, 22, 19), Gba(31, 31, 29));
         WriteRow(SkinTable, 2, "Tan", Gba(26, 20, 14), Gba(21, 15, 10), Gba(29, 24, 18));
         WriteRow(SkinTable, 3, "Sky Blue", Gba(4, 20, 31), Gba(2, 14, 26), Gba(12, 26, 31));
         WriteRow(ClothesTable, 0, "Original");
         WriteRow(ClothesTable, 1, "Red", Gba(31, 4, 4), Gba(24, 2, 2), Gba(31, 12, 12));
         WriteRow(ClothesTable, 2, "Blue", Gba(4, 8, 31), Gba(2, 4, 24), Gba(12, 16, 31));
         Data[SkinCount] = 4;
         Data[ClothesCount] = 3;
         Data[RoleCount] = 6;

         // gender, context, skin main/shadow/highlight/outline, clothes main/shadow/highlight (FF = not painted)
         WriteRole(0, 0, 0, new[] { 2, 3, 1, 4 }, new[] { 10, 11, 0xFF });
         WriteRole(1, 1, 0, new[] { 2, 3, 1, 4 }, new[] { 10, 11, 0xFF });
         WriteRole(2, 0, 1, new[] { 2, 3, 1, 4 }, new[] { 10, 11, 0xFF });
         WriteRole(3, 1, 1, new[] { 2, 3, 1, 4 }, new[] { 10, 11, 0xFF });
         WriteRole(4, 0, 2, new[] { 5, 6, 7, 0xFF }, new[] { 0xFF, 0xFF, 0xFF });
         WriteRole(5, 1, 2, new[] { 5, 6, 7, 0xFF }, new[] { 0xFF, 0xFF, 0xFF });

         AddPointer(PoolSkin, SkinTable);
         AddPointer(PoolClothes, ClothesTable);
         AddPointer(PoolRoles, RoleTable);
         ViewPort.ChangeHistory.ChangeCompleted(); // setting up is not something undo should take back
      }

      protected void WriteRow(int table, int index, string name, params ushort[] colors) {
         var address = table + RowLength * index;
         var bytes = Model.TextConverter.Convert(name, out _);
         for (int i = 0; i < 16; i++) Data[address + i] = i < bytes.Count ? bytes[i] : (byte)0xFF;
         for (int i = 0; i < 3; i++) Model.WriteMultiByteValue(address + 16 + 2 * i, 2, Token, i < colors.Length ? colors[i] : 0);
         Model.WriteMultiByteValue(address + 22, 2, Token, 0);
      }

      protected void WriteRole(int index, int gender, int context, int[] skin, int[] clothes) {
         var address = RoleTable + 12 * index;
         Data[address] = (byte)gender;
         Data[address + 1] = (byte)context;
         for (int i = 0; i < 4; i++) Data[address + 2 + i] = (byte)skin[i];
         for (int i = 0; i < 3; i++) Data[address + 6 + i] = (byte)clothes[i];
         for (int i = 9; i < 12; i++) Data[address + i] = 0;
      }

      protected CharacterCustomization Create() => new CharacterCustomization(Model, () => ViewPort.CurrentChange);

      protected void Done() => ViewPort.ChangeHistory.ChangeCompleted();

      protected void Undo() => ViewPort.Undo.Execute(null);

      protected void Redo() => ViewPort.Redo.Execute(null);

      protected int Word(int address) => Model.ReadMultiByteValue(address, 4);
   }

   public class CharacterCustomizationTests : CharacterCustomizationTestBase {
      #region Support and reading

      [Fact]
      public void ModelWithoutTheAnchors_IsNotSupported() {
         var plain = new PokemonModel(new byte[0x200], singletons: Singletons);

         Assert.False(CharacterCustomization.IsSupported(plain));
         Assert.True(CharacterCustomization.IsSupported(Model));
      }

      [Fact]
      public void MissingCounterAnchor_IsNotSupported() {
         var model = new PokemonModel(new byte[0x400], singletons: Singletons);
         var viewPort = new ViewPort("other.txt", model, InstantDispatch.Instance, Singletons);
         viewPort.Edit($"@100 ^{CharacterCustomization.SkinTonesAnchor}[name\"\"16 main:|c shadow:|c highlight:|c unused:]2 ");
         viewPort.Edit($"@200 ^{CharacterCustomization.ClothesAnchor}[name\"\"16 main:|c shadow:|c highlight:|c unused:]2 ");
         viewPort.Edit($"@300 ^{CharacterCustomization.RolesAnchor}[gender. context. a. b. c. d. e. f. g. h. i. j.]1 ");

         Assert.False(CharacterCustomization.IsSupported(model));
      }

      [Fact]
      public void ReadRows_ReadsNamesAndGbaColors() {
         var skin = Create().SkinTones;

         var rows = skin.ReadRows();

         Assert.Equal(4, rows.Count);
         Assert.Equal(new[] { "Original", "Fair", "Tan", "Sky Blue" }, rows.Select(row => row.Name));
         Assert.Equal(new[] { 0, 1, 2, 3 }, rows.Select(row => row.Index));
         Assert.Equal(new[] { Gba(31, 28, 25), Gba(26, 22, 19), Gba(31, 31, 29) }, rows[1].Colors);
         Assert.Equal("4:20:31", GbaColor.Describe(rows[3].Colors[0]));
         Assert.True(skin.IsHealthy);
         Assert.Equal(4, skin.StoredCount);
         Assert.Equal(3, Create().Clothes.ReadRows().Count);
      }

      [Fact]
      public void ReadRoles_ReadsTheSlots() {
         var roles = Create().ReadRoles();

         Assert.Equal(6, roles.Count);
         var maleOverworld = Create().FindRole(CharacterRole.Male, CharacterRole.OverworldContext);
         Assert.Equal(new[] { 2, 3, 1, 4 }, maleOverworld.Skin);
         Assert.Equal(new[] { 10, 11, -1 }, maleOverworld.Clothes);
         var femaleReflection = Create().FindRole(CharacterRole.Female, CharacterRole.ReflectionContext);
         Assert.Equal(new[] { 5, 6, 7, -1 }, femaleReflection.Skin);
         Assert.Equal(new[] { -1, -1, -1 }, femaleReflection.Clothes);
         Assert.Equal("palette slots 1-4 are painted with the skin tone, slots 10-11 with the clothes colour", maleOverworld.Describe());
         Assert.Equal("Skin: main slot 2, shadow slot 3, highlight slot 1, outline slot 4 (made from the shadow). Clothes: main slot 10, shadow slot 11.", maleOverworld.DescribeSlots());
      }

      [Fact]
      public void RoleCountOfZero_ListsNoRoles() {
         Data[RoleCount] = 0;

         Assert.Empty(Create().ReadRoles());
      }

      #endregion

      #region Colours

      [Fact]
      public void GbaColor_PacksFiveBitsPerChannelAndSwapsRedAndBlueForDrawing() {
         var color = GbaColor.Pack(31, 10, 2);

         Assert.Equal(31 | (10 << 5) | (2 << 10), color);
         Assert.Equal((31, 10, 2), GbaColor.Unpack(color));
         Assert.Equal("31:10:2", GbaColor.Describe(color));
         Assert.Equal((short)((31 << 10) | (10 << 5) | 2), GbaColor.ToDisplay(color));
         Assert.Equal(color, GbaColor.FromDisplay(GbaColor.ToDisplay(color)));
         Assert.Equal(GbaColor.Pack(31, 31, 0), GbaColor.Pack(99, 40, -5)); // out of range channels are limited
      }

      [Fact]
      public void DeriveShades_MakesTheShadowDarkerAndTheHighlightLighter_InWholeGbaSteps() {
         var main = Gba(20, 16, 10);

         var (shadow, highlight) = GbaColor.DeriveShades(main);

         var (sr, sg, sb) = GbaColor.Unpack(shadow);
         var (hr, hg, hb) = GbaColor.Unpack(highlight);
         Assert.True(sr < 20 && sg < 16 && sb < 10);
         Assert.True(hr > 20 && hg > 16 && hb > 10);
         Assert.True(hr <= 31 && hg <= 31 && hb <= 31);
      }

      [Fact]
      public void SetColor_WritesTheGbaColorToTheRow_AndUndoTakesItBack() {
         var skin = Create().SkinTones;
         var before = skin.ReadRow(2).Colors[1];

         var result = skin.SetColor(2, 1, Gba(31, 0, 4));
         Done();

         Assert.True(result.Success);
         Assert.Equal(Gba(31, 0, 4), skin.ReadRow(2).Colors[1]);
         Assert.Equal(Gba(31, 0, 4), Model.ReadMultiByteValue(SkinTable + RowLength * 2 + 18, 2));
         Assert.Equal(31 | (4 << 10), Model[SkinTable + RowLength * 2 + 18] | (Model[SkinTable + RowLength * 2 + 19] << 8)); // red in the low bits: how the GBA stores it
         Undo();
         Assert.Equal(before, skin.ReadRow(2).Colors[1]);
      }

      [Fact]
      public void SetColors_ChangesAllThree_InOneUndoStep() {
         var skin = Create().SkinTones;

         skin.SetColors(1, new[] { Gba(1, 2, 3), Gba(4, 5, 6), Gba(7, 8, 9) });
         Done();
         Assert.Equal(new[] { Gba(1, 2, 3), Gba(4, 5, 6), Gba(7, 8, 9) }, skin.ReadRow(1).Colors);

         Undo();
         Assert.Equal(new[] { Gba(31, 28, 25), Gba(26, 22, 19), Gba(31, 31, 29) }, skin.ReadRow(1).Colors);
      }

      [Fact]
      public void SetColor_RowZeroIsTheOriginalLook_AndCannotBeEdited() {
         var skin = Create().SkinTones;

         var result = skin.SetColor(0, 0, Gba(1, 1, 1));

         Assert.False(result.Success);
         Assert.Contains("Row 0", result.Message);
         Assert.Equal(0, skin.ReadRow(0).Colors[0]);
      }

      [Fact]
      public void DeriveShades_FromTheMainColourOfARow() {
         var skin = Create().SkinTones;
         var main = skin.ReadRow(2).Colors[0];

         var result = skin.DeriveShades(2);

         var expected = GbaColor.DeriveShades(main);
         Assert.True(result.Success);
         Assert.Equal(main, skin.ReadRow(2).Colors[0]);
         Assert.Equal(expected.shadow, skin.ReadRow(2).Colors[1]);
         Assert.Equal(expected.highlight, skin.ReadRow(2).Colors[2]);
      }

      [Fact]
      public void TableColorFields_ShowTheColourTheGbaShows_AndWriteTheGbaOrder() {
         var run = (ITableRun)Model.GetNextRun(SkinTable);
         var segment = run.ElementContent.OfType<ArrayRunColorSegment>().First();
         var address = SkinTable + RowLength + 16; // 'Fair' main: 31:28:25

         Assert.Equal("31:28:25", segment.ToText(Model, address, 0));
         var field = new ColorFieldArrayElementViewModel(ViewPort, "main", address);
         Assert.Equal("31:28:25", field.Content);
         Assert.Equal(GbaColor.ToDisplay(Gba(31, 28, 25)), field.Color);

         field.Content = "31:0:4";
         Done();

         Assert.Equal(Gba(31, 0, 4), Model.ReadMultiByteValue(address, 2));
         Assert.Equal("31:0:4", segment.ToText(Model, address, 0));

         field.Color = GbaColor.ToDisplay(Gba(2, 20, 30));
         Done();

         Assert.Equal(Gba(2, 20, 30), Model.ReadMultiByteValue(address, 2));
      }

      #endregion

      #region Applying a role

      private static IReadOnlyList<short> TestPalette() => Enumerable.Range(0, 16).Select(i => (short)(100 * (i + 1))).ToArray();

      [Fact]
      public void Apply_PaintsTheSkinAndClothesSlots_AndLeavesTheOtherSlotsAlone() {
         var customization = Create();
         var role = customization.FindRole(CharacterRole.Male, CharacterRole.OverworldContext);
         var skin = customization.SkinTones.ReadRow(2);
         var clothes = customization.Clothes.ReadRow(1);
         var palette = TestPalette();

         var painted = role.Apply(palette, skin, clothes);

         Assert.Equal(GbaColor.ToDisplay(skin.Colors[0]), painted[2]);
         Assert.Equal(GbaColor.ToDisplay(skin.Colors[1]), painted[3]);
         Assert.Equal(GbaColor.ToDisplay(skin.Colors[2]), painted[1]);
         Assert.Equal(GbaColor.ToDisplay(CharacterRole.OutlineFromShadow(skin.Colors[1])), painted[4]);
         Assert.Equal(GbaColor.ToDisplay(clothes.Colors[0]), painted[10]);
         Assert.Equal(GbaColor.ToDisplay(clothes.Colors[1]), painted[11]);
         foreach (var untouched in new[] { 0, 5, 6, 7, 8, 9, 12, 13, 14, 15 }) Assert.Equal(palette[untouched], painted[untouched]);
         Assert.Equal(TestPalette(), palette); // the palette that was passed in is not changed
      }

      [Fact]
      public void Apply_RowZeroOfBothTables_LeavesThePaletteExactlyAsItWas() {
         var customization = Create();
         var role = customization.FindRole(CharacterRole.Female, CharacterRole.TrainerPicContext);

         var painted = role.Apply(TestPalette(), customization.SkinTones.ReadRow(0), customization.Clothes.ReadRow(0));

         Assert.Equal(TestPalette(), painted);
      }

      [Fact]
      public void Apply_RowZeroOfOnlyOneTable_StillPaintsTheOther() {
         var customization = Create();
         var role = customization.FindRole(CharacterRole.Male, CharacterRole.OverworldContext);
         var clothes = customization.Clothes.ReadRow(2);

         var painted = role.Apply(TestPalette(), customization.SkinTones.ReadRow(0), clothes);

         Assert.Equal(TestPalette()[2], painted[2]);
         Assert.Equal(GbaColor.ToDisplay(clothes.Colors[0]), painted[10]);
      }

      [Fact]
      public void OutlineFromShadow_IsNineSixteenthsOfEveryChannel() {
         Assert.Equal("17:11:4", GbaColor.Describe(CharacterRole.OutlineFromShadow(Gba(31, 20, 8))));
         Assert.Equal("0:0:0", GbaColor.Describe(CharacterRole.OutlineFromShadow(Gba(0, 0, 1))));
      }

      [Fact]
      public void FormatSlots_JoinsRuns() {
         Assert.Equal("1-4, 10", CharacterRole.FormatSlots(new[] { 4, 1, 3, 2, 10, -1 }));
         Assert.Equal("3", CharacterRole.FormatSlots(new[] { 3, 3 }));
         Assert.Equal(string.Empty, CharacterRole.FormatSlots(new[] { -1, -1 }));
      }

      #endregion

      #region Names

      [Fact]
      public void SetName_WritesGameTextPaddedWithTerminators_AndUndoTakesItBack() {
         var skin = Create().SkinTones;

         var result = skin.SetName(2, "Sunburnt");
         Done();

         Assert.True(result.Success);
         Assert.Equal("Sunburnt", skin.ReadRow(2).Name);
         var address = SkinTable + RowLength * 2;
         Assert.Equal(0xFF, Model[address + 8]); // the terminator
         Assert.Equal(0xFF, Model[address + 15]); // and the padding
         Undo();
         Assert.Equal("Tan", skin.ReadRow(2).Name);
      }

      [Fact]
      public void SetName_AcceptsFifteenCharacters_AndRefusesSixteen() {
         var skin = Create().SkinTones;

         Assert.True(skin.SetName(1, "123456789012345").Success);
         var tooLong = skin.SetName(1, "1234567890123456");

         Assert.False(tooLong.Success);
         Assert.Contains("15", tooLong.Message);
         Assert.Equal("123456789012345", skin.ReadRow(1).Name);
         Assert.Equal(0xFF, Model[SkinTable + RowLength + 15]); // the terminator is still there: the next field is not touched
         Assert.Equal(Gba(31, 28, 25), skin.ReadRow(1).Colors[0]);
      }

      [Fact]
      public void SetName_RefusesEmptyNamesAndTheOriginalRow() {
         var skin = Create().SkinTones;

         Assert.False(skin.SetName(1, "   ").Success);
         Assert.False(skin.SetName(0, "Mine").Success);
         Assert.False(skin.SetName(9, "Nowhere").Success);
         Assert.Equal("Fair", skin.ReadRow(1).Name);
         Assert.Equal("Original", skin.ReadRow(0).Name);
      }

      [Fact]
      public void UniqueName_AddsANumberAndStaysInsideFifteenCharacters() {
         var skin = Create().SkinTones;

         Assert.Equal("Fair 2", skin.UniqueName("Fair"));
         Assert.Equal("Brand New", skin.UniqueName("Brand New"));
         Assert.Equal("New skin tone", skin.UniqueName(""));
         var longName = skin.UniqueName("A name that is far too long");
         Assert.True(longName.Length <= CharacterColorTable.MaxNameLength);
      }

      #endregion

      #region Adding, duplicating, removing, moving

      [Fact]
      public void Add_ToATableWithRoomBehindIt_GrowsInPlaceAndRaisesTheCounter() {
         var clothes = Create().Clothes;

         var result = clothes.Add("Green", new[] { Gba(4, 28, 6), Gba(2, 20, 4), Gba(12, 31, 14) });
         Done();

         Assert.True(result.Success);
         Assert.Equal(3, result.Index);
         Assert.Equal(ClothesTable, clothes.Run.Start);
         Assert.Equal(4, clothes.Run.ElementCount);
         Assert.Equal(4, Model[ClothesCount]);
         Assert.Equal("Green", clothes.ReadRow(3).Name);
         Assert.Equal(Gba(4, 28, 6), clothes.ReadRow(3).Colors[0]);
         Assert.True(clothes.IsHealthy);
         Assert.Equal(ClothesTable, Model.ReadPointer(PoolClothes));
      }

      [Fact]
      public void Add_ToATableThatCannotGrowInPlace_MovesItAndUpdatesEveryReference() {
         var skin = Create().SkinTones;
         var skinBefore = skin.ReadRows().Select(row => (row.Name, row.Colors.ToArray())).ToList();

         var result = skin.Add("Mint", new[] { Gba(10, 28, 18), Gba(6, 22, 12), Gba(16, 31, 24) });
         Done();

         Assert.True(result.Success);
         Assert.Contains("moved to free space", result.Message);
         Assert.NotEqual(SkinTable, skin.Run.Start);
         Assert.Equal(5, skin.Run.ElementCount);
         Assert.Equal(skin.Run.Start, Model.ReadPointer(PoolSkin)); // the game's reference follows the table
         Assert.Equal(skin.Run.Start, skin.TableAddress);
         Assert.Equal(5, Model[SkinCount]);
         for (int i = 0; i < skinBefore.Count; i++) {
            Assert.Equal(skinBefore[i].Name, skin.ReadRow(i).Name);
            Assert.Equal(skinBefore[i].Item2, skin.ReadRow(i).Colors);
         }
         Assert.Equal("Mint", skin.ReadRow(4).Name);
         Assert.Equal(ClothesTable, Create().Clothes.Run.Start); // the neighbour did not move
      }

      [Fact]
      public void Add_ThatMovedTheTable_IsOneUndoStepAndRedoRepeatsIt() {
         var skin = Create().SkinTones;

         skin.Add("Mint", new[] { Gba(10, 28, 18), Gba(6, 22, 12), Gba(16, 31, 24) });
         Done();
         var moved = skin.Run.Start;
         Undo();

         Assert.Equal(SkinTable, skin.Run.Start);
         Assert.Equal(4, skin.Run.ElementCount);
         Assert.Equal(SkinTable, Model.ReadPointer(PoolSkin));
         Assert.Equal(4, Model[SkinCount]);
         Assert.Equal(4, skin.ReadRows().Count);

         Redo();

         Assert.Equal(moved, skin.Run.Start);
         Assert.Equal(5, skin.Run.ElementCount);
         Assert.Equal(moved, Model.ReadPointer(PoolSkin));
         Assert.Equal(5, Model[SkinCount]);
         Assert.Equal("Mint", skin.ReadRow(4).Name);
      }

      [Fact]
      public void Add_RefusesABadName_AndChangesNothing() {
         var skin = Create().SkinTones;

         var result = skin.Add("This name is much too long", new[] { Gba(1, 1, 1), Gba(1, 1, 1), Gba(1, 1, 1) });

         Assert.False(result.Success);
         Assert.Equal(4, skin.Run.ElementCount);
         Assert.Equal(4, Model[SkinCount]);
         Assert.Equal(SkinTable, skin.Run.Start);
      }

      [Fact]
      public void Add_StopsAtTheLimitOfTheCounterByte() {
         var skin = Create().SkinTones;
         int guard = 0;
         while (skin.StoredCount < CharacterColorTable.MaxRows && guard++ < 300) {
            var result = skin.Add($"Tone {skin.StoredCount}", new[] { Gba(1, 2, 3), Gba(1, 2, 3), Gba(1, 2, 3) });
            Assert.True(result.Success, result.Message);
         }
         Done();

         Assert.Equal(255, skin.StoredCount);
         Assert.Equal(255, skin.Run.ElementCount);
         Assert.True(skin.IsHealthy);
         var tooMany = skin.Add("One too many", new[] { Gba(1, 2, 3), Gba(1, 2, 3), Gba(1, 2, 3) });
         Assert.False(tooMany.Success);
         Assert.Contains("255", tooMany.Message);
         Assert.Equal(255, Model[SkinCount]);
         Assert.Equal(skin.Run.Start, Model.ReadPointer(PoolSkin));
         Assert.Equal("Tone 254", skin.ReadRow(254).Name);
      }

      [Fact]
      public void Duplicate_CopiesTheColoursWithAUniqueName() {
         var clothes = Create().Clothes;

         var result = clothes.Duplicate(2);
         Done();

         Assert.True(result.Success);
         Assert.Equal(3, result.Index);
         Assert.Equal("Blue 2", clothes.ReadRow(3).Name);
         Assert.Equal(clothes.ReadRow(2).Colors, clothes.ReadRow(3).Colors);
         Assert.Equal(4, Model[ClothesCount]);
         Undo();
         Assert.Equal(3, clothes.ReadRows().Count);
         Assert.Equal(3, Model[ClothesCount]);
      }

      [Fact]
      public void Remove_ShiftsTheLaterRowsUp_ShortensTheTable_AndUpdatesTheCounter() {
         var skin = Create().SkinTones;

         var result = skin.Remove(2);
         Done();

         Assert.True(result.Success);
         Assert.Equal(3, skin.Run.ElementCount);
         Assert.Equal(3, Model[SkinCount]);
         Assert.Equal(new[] { "Original", "Fair", "Sky Blue" }, skin.ReadRows().Select(row => row.Name));
         Assert.Equal(Gba(4, 20, 31), skin.ReadRow(2).Colors[0]);
         Assert.True(skin.IsHealthy);
         Undo();
         Assert.Equal(new[] { "Original", "Fair", "Tan", "Sky Blue" }, skin.ReadRows().Select(row => row.Name));
         Assert.Equal(4, Model[SkinCount]);
      }

      [Fact]
      public void Remove_NeverRemovesRowZeroOrAMissingRow() {
         var skin = Create().SkinTones;

         Assert.False(skin.Remove(0).Success);
         Assert.False(skin.Remove(4).Success);
         Assert.False(skin.Remove(-1).Success);
         Assert.Equal(4, skin.Run.ElementCount);
         Assert.Equal(4, Model[SkinCount]);
      }

      [Fact]
      public void Remove_DownToJustTheOriginalRow_IsAllowedButNotBelow() {
         var clothes = Create().Clothes;

         Assert.True(clothes.Remove(2).Success);
         Assert.True(clothes.Remove(1).Success);
         Assert.False(clothes.Remove(0).Success);

         Assert.Equal(1, clothes.Run.ElementCount);
         Assert.Equal(1, Model[ClothesCount]);
         Assert.Equal("Original", clothes.ReadRow(0).Name);
      }

      [Fact]
      public void Move_SwapsTwoRows_ButNeverMovesRowZero() {
         var skin = Create().SkinTones;

         var result = skin.Move(2, 1);
         Done();

         Assert.True(result.Success);
         Assert.Equal(3, result.Index);
         Assert.Equal(new[] { "Original", "Fair", "Sky Blue", "Tan" }, skin.ReadRows().Select(row => row.Name));
         Assert.Equal(Gba(26, 20, 14), skin.ReadRow(3).Colors[0]);
         Assert.False(skin.Move(1, -1).Success);
         Assert.False(skin.Move(0, 1).Success);
         Assert.False(skin.Move(3, 1).Success);
         Undo();
         Assert.Equal(new[] { "Original", "Fair", "Tan", "Sky Blue" }, skin.ReadRows().Select(row => row.Name));
      }

      #endregion

      #region Counters that do not match

      [Fact]
      public void CounterOfZero_IsAProblem_ThatBlocksAddRemoveAndMove_UntilFixed() {
         var clothes = Create().Clothes;
         Data[ClothesCount] = 0;

         Assert.False(clothes.IsHealthy);
         Assert.Contains("0", clothes.Problem);
         Assert.Contains("Fix", clothes.Problem);
         Assert.Equal(3, clothes.VisibleRows);
         Assert.False(clothes.Add("X", new ushort[3]).Success);
         Assert.False(clothes.Remove(1).Success);
         Assert.False(clothes.Move(1, 1).Success);
         Assert.Equal(3, clothes.Run.ElementCount);

         var fixedResult = clothes.FixCount();
         Done();

         Assert.True(fixedResult.Success);
         Assert.Equal(3, Model[ClothesCount]);
         Assert.True(clothes.IsHealthy);
         Undo();
         Assert.Equal(0, Model[ClothesCount]);
      }

      [Fact]
      public void CounterBiggerThanTheTable_OnlyRealRowsAreShown() {
         Data[SkinCount] = 200;
         var skin = Create().SkinTones;

         Assert.False(skin.IsHealthy);
         Assert.Equal(4, skin.VisibleRows);
         Assert.Equal(4, skin.ReadRows().Count);
         Assert.True(skin.FixCount().Success);
         Assert.Equal(4, Model[SkinCount]);
      }

      [Fact]
      public void CounterSmallerThanTheTable_OnlyTheCountedRowsAreShown() {
         Data[SkinCount] = 2;
         var skin = Create().SkinTones;

         Assert.False(skin.IsHealthy);
         Assert.Equal(2, skin.VisibleRows);
         Assert.Equal(2, skin.ReadRows().Count);
         Assert.True(skin.FixCount().Success);
         Assert.Equal(4, Model[SkinCount]);
         Assert.Equal(4, skin.ReadRows().Count);
      }

      [Fact]
      public void RowsTooShortForANameAndThreeColours_AreAProblemNotACrash() {
         var model = new PokemonModel(new byte[0x400], singletons: Singletons);
         var viewPort = new ViewPort("other.txt", model, InstantDispatch.Instance, Singletons);
         viewPort.Edit($"@100 ^{CharacterCustomization.SkinTonesAnchor}[a:: b::: c:: d:]2 ");
         viewPort.Edit($"@200 ^{CharacterCustomization.SkinToneCountAnchor}[count.]1 ");
         var table = new CharacterColorTable(model, () => viewPort.CurrentChange, CharacterColorKind.SkinTone);

         Assert.False(table.IsHealthy);
         Assert.Equal(0, table.VisibleRows);
         Assert.Empty(table.ReadRows());
         Assert.False(table.SetName(1, "x").Success);
      }

      [Fact]
      public void TableThatIsMissing_IsAProblemNotACrash() {
         var customization = new CharacterCustomization(new PokemonModel(new byte[0x100], singletons: Singletons), () => new ModelDelta());

         Assert.False(customization.SkinTones.Exists);
         Assert.False(customization.SkinTones.IsHealthy);
         Assert.Contains("no skin tone table", customization.SkinTones.Problem);
         Assert.Empty(customization.SkinTones.ReadRows());
         Assert.Empty(customization.ReadRoles());
         Assert.False(customization.SkinTones.Add("X", new ushort[3]).Success);
      }

      #endregion
   }

   public class CharacterCustomizationTabTests : CharacterCustomizationTestBase {
      private readonly List<string> tabErrors = new();
      private readonly List<string> tabMessages = new();

      private CharacterCustomizationTab CreateTab() {
         var tab = new CharacterCustomizationTab(ViewPort);
         tab.OnError += (sender, e) => tabErrors.Add(e);
         tab.OnMessage += (sender, e) => tabMessages.Add(e);
         return tab;
      }

      [Fact]
      public void Tab_IsSupportedOnlyWhereTheTablesAre() {
         Assert.True(CharacterCustomizationTab.IsSupported(Model));
         Assert.False(CharacterCustomizationTab.IsSupported(new PokemonModel(new byte[0x100], singletons: Singletons)));
      }

      [Fact]
      public void OpeningTheTabWithoutTheTables_SaysWhyAndOpensNothing() {
         var plain = new PokemonModel(new byte[0x200], singletons: Singletons);
         var viewPort = new ViewPort("plain.txt", plain, InstantDispatch.Instance, Singletons);
         var errors = new List<string>();
         var opened = new List<ITabContent>();
         viewPort.OnError += (sender, e) => errors.Add(e);
         viewPort.RequestTabChange += (sender, e) => opened.Add(e.NewTab);

         viewPort.OpenCharacterCustomizationTab();

         Assert.Empty(opened);
         Assert.Single(errors);
         Assert.Contains("no character customization tables", errors[0]);
      }

      [Fact]
      public void Tab_ListsBothTables_WithTheOriginalRowFirstAndNotEditable() {
         var tab = CreateTab();

         Assert.Equal(4, tab.SkinTones.Rows.Count);
         Assert.Equal(3, tab.Clothes.Rows.Count);
         Assert.True(tab.SkinTones.Rows[0].IsOriginal);
         Assert.False(tab.SkinTones.Rows[0].IsEditable);
         Assert.Equal("Original (the sprite's own colours)", tab.SkinTones.Rows[0].Caption);
         Assert.Equal("Tan", tab.SkinTones.Rows[2].Caption);
         Assert.True(tab.SkinTones.Rows[2].IsEditable);
         Assert.Equal(0, tab.SkinTones.SelectedIndex);
         Assert.False(tab.SkinTones.CanEditSelected);
         Assert.Equal(GbaColor.ToDisplay(Gba(31, 4, 4)), tab.Clothes.Rows[1].Main);
         Assert.False(tab.SkinTones.HasProblem);
      }

      [Fact]
      public void Tab_WithoutTheSpriteTables_StillWorksAndSaysWhatIsMissing() {
         var tab = CreateTab();

         Assert.All(tab.Panels, panel => Assert.True(panel.HasNote));
         Assert.All(tab.Panels, panel => Assert.False(panel.Standing.HasImage));
         tab.SkinTones.Select(2);
         tab.Clothes.Select(1);
         Assert.Empty(tabErrors);
         Assert.Equal(2, tab.SkinTones.SelectedIndex);
      }

      [Fact]
      public void Tab_SelectingARow_ShowsItsThreeColoursInThePaletteEditor() {
         var tab = CreateTab();

         tab.SkinTones.Select(3);

         Assert.Equal(3, tab.SkinTones.Palette.Elements.Count);
         Assert.Equal(GbaColor.ToDisplay(Gba(4, 20, 31)), tab.SkinTones.Palette.Elements[0].Color);
         Assert.Equal(GbaColor.ToDisplay(Gba(2, 14, 26)), tab.SkinTones.Palette.Elements[1].Color);
         Assert.True(tab.SkinTones.Palette.CanEditColors);
      }

      [Fact]
      public void Tab_EditingAColourInThePaletteEditor_ReachesTheRomAndUndoTakesItBack() {
         var tab = CreateTab();
         tab.SkinTones.Select(2);
         var before = tab.SkinTones.Data[2].Colors[1];
         var edited = Gba(5, 9, 28);

         tab.SkinTones.Palette.Elements[1].Color = GbaColor.ToDisplay(edited);
         tab.SkinTones.Palette.PushColorsToModel();

         Assert.Equal(edited, tab.SkinTones.Data[2].Colors[1]);
         Assert.Equal(edited, Model.ReadMultiByteValue(SkinTable + RowLength * 2 + 18, 2));
         Assert.Equal(GbaColor.ToDisplay(edited), tab.SkinTones.Rows[2].Shadow);
         Assert.Contains("5:9:28", tab.SkinTones.ColorSummary);

         tab.Undo.Execute(null);

         Assert.Equal(before, tab.SkinTones.Data[2].Colors[1]);
         Assert.Equal(GbaColor.ToDisplay(before), tab.SkinTones.Palette.Elements[1].Color);
      }

      [Fact]
      public void Tab_TheOriginalRowIsNeverWritten() {
         var tab = CreateTab();
         tab.SkinTones.Select(0);

         tab.SkinTones.Palette.Elements[0].Color = 1234;
         tab.SkinTones.Palette.PushColorsToModel();

         Assert.Equal(0, Model.ReadMultiByteValue(SkinTable + 16, 2));
      }

      [Fact]
      public void Tab_DeriveButton_MakesTheShadeAndHighlightFromTheMainColour() {
         var tab = CreateTab();
         tab.SkinTones.Select(2);
         var main = tab.SkinTones.Data[2].Colors[0];

         tab.SkinTones.DeriveShadeCommand.Execute(null);

         var expected = GbaColor.DeriveShades(main);
         Assert.Equal(main, tab.SkinTones.Data[2].Colors[0]);
         Assert.Equal(expected.shadow, tab.SkinTones.Data[2].Colors[1]);
         Assert.Equal(expected.highlight, tab.SkinTones.Data[2].Colors[2]);
      }

      [Fact]
      public void Tab_RenamingARow_WritesTheName_AndUndoTakesItBack() {
         var tab = CreateTab();

         var accepted = tab.SkinTones.Rows[2].Rename("Sunny");

         Assert.True(accepted);
         Assert.Equal("Sunny", tab.SkinTones.Rows[2].Name);
         Assert.Equal("Sunny", tab.SkinTones.Table.ReadRow(2).Name);
         tab.Undo.Execute(null);
         Assert.Equal("Tan", tab.SkinTones.Rows[2].Name);
         Assert.Empty(tabErrors);
      }

      [Fact]
      public void Tab_ANameThatIsTooLongOrOnTheOriginalRow_IsRefusedWithAMessage() {
         var tab = CreateTab();

         Assert.False(tab.SkinTones.Rows[2].Rename("A name that is far too long"));
         Assert.Single(tabErrors);
         Assert.Equal("Tan", tab.SkinTones.Rows[2].Name);
         tabErrors.Clear();

         Assert.False(tab.SkinTones.Rows[0].Rename("Mine"));
         Assert.Equal("Original", tab.SkinTones.Rows[0].Name);
      }

      [Fact]
      public void Tab_AddDuplicateMoveAndRemoveButtons_KeepTheCounterAndTheListsInStep() {
         var tab = CreateTab();
         var clothes = tab.Clothes;

         clothes.Select(2);
         clothes.Duplicate.Execute(null);
         Assert.Equal(4, clothes.Rows.Count);
         Assert.Equal(3, clothes.SelectedIndex);
         Assert.Equal("Blue 2", clothes.Rows[3].Name);
         Assert.Equal(4, Model[ClothesCount]);
         Assert.Equal(4, tab.ClothesGrid.Count);

         clothes.Add.Execute(null);
         Assert.Equal(5, clothes.Rows.Count);
         Assert.Equal("New colour", clothes.Rows[4].Name);
         Assert.Equal(4, clothes.SelectedIndex);

         clothes.MoveUp.Execute(null);
         Assert.Equal(3, clothes.SelectedIndex);
         Assert.Equal("New colour", clothes.Rows[3].Name);

         clothes.Remove.Execute(null);
         Assert.Equal(4, clothes.Rows.Count);
         Assert.DoesNotContain(clothes.Rows, row => row.Name == "New colour");

         clothes.Select(0);
         Assert.False(clothes.Remove.CanExecute(null));
         Assert.False(clothes.MoveUp.CanExecute(null));
         Assert.False(clothes.DeriveShadeCommand.CanExecute(null));

         tab.Undo.Execute(null); // the remove
         tab.Undo.Execute(null); // the move
         tab.Undo.Execute(null); // the add
         tab.Undo.Execute(null); // the duplicate
         Assert.Equal(3, clothes.Rows.Count);
         Assert.Equal(3, Model[ClothesCount]);
         Assert.Equal(3, tab.ClothesGrid.Count);
      }

      [Fact]
      public void Tab_AddingToATableThatMustMove_TellsThePersonAndUpdatesTheGameReference() {
         var tab = CreateTab();

         tab.SkinTones.Add.Execute(null);

         Assert.Equal(5, tab.SkinTones.Rows.Count);
         Assert.Equal("New skin tone", tab.SkinTones.Rows[4].Name);
         Assert.NotEqual(SkinTable, tab.SkinTones.Table.Run.Start);
         Assert.Equal(tab.SkinTones.Table.Run.Start, Model.ReadPointer(PoolSkin));
         Assert.Contains(tabMessages, message => message.Contains("moved to free space"));

         tab.Undo.Execute(null);

         Assert.Equal(SkinTable, tab.SkinTones.Table.Run.Start);
         Assert.Equal(SkinTable, Model.ReadPointer(PoolSkin));
         Assert.Equal(4, tab.SkinTones.Rows.Count);
      }

      [Fact]
      public void Tab_ABrokenCounter_ShowsAProblem_DisablesTheButtons_AndTheFixButtonRepairsIt() {
         Data[ClothesCount] = 0;
         var tab = CreateTab();
         var clothes = tab.Clothes;

         Assert.True(clothes.HasProblem);
         Assert.Equal(3, clothes.Rows.Count);
         Assert.False(clothes.Add.CanExecute(null));
         Assert.False(clothes.Remove.CanExecute(null));
         Assert.True(clothes.FixCount.CanExecute(null));

         clothes.FixCount.Execute(null);

         Assert.False(clothes.HasProblem);
         Assert.Equal(3, Model[ClothesCount]);
         tab.Undo.Execute(null);
         Assert.True(clothes.HasProblem);
      }

      [Fact]
      public void Tab_ZoomStaysBetweenOneAndSix() {
         var tab = CreateTab();

         tab.Zoom = 5;
         Assert.Equal(5, tab.Zoom);
         tab.Zoom = 99;
         Assert.Equal(6, tab.Zoom);
         tab.Zoom = -3;
         Assert.Equal(1, tab.Zoom);
      }

      [Fact]
      public void Tab_PreviewAllGrids_HaveATilePerRow_AndCanBeHidden() {
         var tab = CreateTab();

         Assert.Equal(4, tab.SkinGrid.Count);
         Assert.Equal(3, tab.ClothesGrid.Count);
         tab.ShowAllPreviews = false;
         Assert.Empty(tab.SkinGrid);
         Assert.Empty(tab.ClothesGrid);
         tab.ShowAllPreviews = true;
         Assert.Equal(4, tab.SkinGrid.Count);
      }

      [Fact]
      public void Tab_RoleLinesAndThePictureHint_SayWhichSlotsTheGamePaints() {
         var tab = CreateTab();

         Assert.NotEmpty(tab.RoleLines);
         Assert.Contains("Skin: main slot 2, shadow slot 3, highlight slot 1, outline slot 4 (made from the shadow).", tab.PictureHint);
         Assert.Contains("slot", string.Join(" ", tab.RoleLines));
      }
   }
}
