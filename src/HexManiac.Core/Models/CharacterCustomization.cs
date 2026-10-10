using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HavenSoft.HexManiac.Core.Models {
   /// <summary>
   /// Colours the way the GBA stores them (0bbbbbgggggrrrrr, five bits per channel) and the way HexManiac draws them
   /// (the same bits with red and blue swapped, like a palette in the image editor).
   /// Everything the character customization stores in the ROM is a GBA colour: only the previews use the drawing order.
   /// </summary>
   public static class GbaColor {
      public const int MaxChannel = 31;

      public static ushort Pack(int red, int green, int blue) {
         red = Math.Max(0, Math.Min(MaxChannel, red));
         green = Math.Max(0, Math.Min(MaxChannel, green));
         blue = Math.Max(0, Math.Min(MaxChannel, blue));
         return (ushort)(red | (green << 5) | (blue << 10));
      }

      public static (int red, int green, int blue) Unpack(ushort color) => (color & 31, (color >> 5) & 31, (color >> 10) & 31);

      /// <summary>The colour as HexManiac's palettes and pictures hold it.</summary>
      public static short ToDisplay(ushort color) {
         var (red, green, blue) = Unpack(color);
         return UncompressedPaletteColor.Pack(red, green, blue);
      }

      /// <summary>The colour as the GBA stores it, from a colour of a HexManiac palette.</summary>
      public static ushort FromDisplay(short color) {
         var (red, green, blue) = UncompressedPaletteColor.ToRGB(color);
         return Pack(red, green, blue);
      }

      /// <summary>"red:green:blue", each 0 to 31: exactly what the GBA can show, no hidden extra precision.</summary>
      public static string Describe(ushort color) {
         var (red, green, blue) = Unpack(color);
         return $"{red}:{green}:{blue}";
      }

      /// <summary>
      /// A starting point for the shadow and the highlight of a colour: the shadow is the colour made darker, the highlight lighter, both kept in whole GBA steps.
      /// </summary>
      public static (ushort shadow, ushort highlight) DeriveShades(ushort main) {
         var (red, green, blue) = Unpack(main);
         int Darker(int channel) => (int)Math.Round(channel * 0.74);
         int Lighter(int channel) => (int)Math.Round(channel + (MaxChannel - channel) * 0.45);
         return (Pack(Darker(red), Darker(green), Darker(blue)), Pack(Lighter(red), Lighter(green), Lighter(blue)));
      }
   }

   /// <summary>One row of the skin tone or clothes colour table: a name and three colours (main, shadow, highlight).</summary>
   public class CharacterColorRow {
      public int Index { get; }
      public string Name { get; }
      /// <summary>Main, shadow and highlight, as the GBA stores them.</summary>
      public IReadOnlyList<ushort> Colors { get; }

      public CharacterColorRow(int index, string name, IReadOnlyList<ushort> colors) {
         Index = index;
         Name = name ?? string.Empty;
         var copy = new ushort[CharacterColorTable.ColorsPerRow];
         for (int i = 0; i < copy.Length && colors != null && i < colors.Count; i++) copy[i] = colors[i];
         Colors = copy;
      }

      public CharacterColorRow With(int index, string name) => new(index, name, Colors);
   }

   /// <summary>
   /// Which palette slots (0 to 15) of one kind of picture receive the colours of the chosen skin tone and clothes colour.
   /// A slot of -1 is not used by that colour. The skin has four slots: main, shadow, highlight and the outline the game makes from the shadow.
   /// The clothes have three: main, shadow and highlight.
   /// </summary>
   public class CharacterRole {
      public const int Male = 0, Female = 1;
      public const int OverworldContext = 0, TrainerPicContext = 1, ReflectionContext = 2;
      public const int SkinSlots = 4, ClothesSlots = 3;

      public int Gender { get; }
      public int Context { get; }
      /// <summary>Main, shadow, highlight, outline.</summary>
      public IReadOnlyList<int> Skin { get; }
      /// <summary>Main, shadow, highlight.</summary>
      public IReadOnlyList<int> Clothes { get; }

      public CharacterRole(int gender, int context, IReadOnlyList<int> skin, IReadOnlyList<int> clothes) {
         (Gender, Context) = (gender, context);
         Skin = Pad(skin, SkinSlots);
         Clothes = Pad(clothes, ClothesSlots);
      }

      private static int[] Pad(IReadOnlyList<int> slots, int length) {
         var result = new int[length];
         for (int i = 0; i < length; i++) result[i] = slots != null && i < slots.Count ? slots[i] : -1;
         return result;
      }

      public string GenderName => Gender == Male ? "Boy" : "Girl";

      public static string ContextName(int context) => context switch {
         OverworldContext => "overworld sprites",
         TrainerPicContext => "trainer pictures (front and back)",
         ReflectionContext => "reflection in water",
         _ => $"picture type {context}",
      };

      /// <summary>
      /// The darkest skin colour, the outline of the skin parts. The game makes it from the shadow: nine sixteenths of every channel, in whole GBA steps.
      /// </summary>
      public static ushort OutlineFromShadow(ushort shadow) {
         var (red, green, blue) = GbaColor.Unpack(shadow);
         return GbaColor.Pack(red * 9 >> 4, green * 9 >> 4, blue * 9 >> 4);
      }

      /// <summary>
      /// The palette the picture is drawn with after the skin tone and the clothes colour were applied.
      /// Row 0 of either table is the original look: the game does not recolour then, so neither does this.
      /// </summary>
      public short[] Apply(IReadOnlyList<short> palette, CharacterColorRow skin, CharacterColorRow clothes) {
         var result = palette.ToArray();
         if (skin != null && skin.Index != 0) {
            var colors = new[] { skin.Colors[0], skin.Colors[1], skin.Colors[2], OutlineFromShadow(skin.Colors[1]) };
            Paint(result, Skin, colors);
         }
         if (clothes != null && clothes.Index != 0) Paint(result, Clothes, clothes.Colors);
         return result;
      }

      private static void Paint(short[] palette, IReadOnlyList<int> slots, IReadOnlyList<ushort> colors) {
         for (int i = 0; i < slots.Count && i < colors.Count; i++) {
            var slot = slots[i];
            if (slot < 0 || slot >= palette.Length) continue;
            palette[slot] = GbaColor.ToDisplay(colors[i]);
         }
      }

      /// <summary>"3" for one slot, "1-3" for a run of slots, "1-4, 10" for several: sorted, without repeats.</summary>
      public static string FormatSlots(IEnumerable<int> slots) {
         var sorted = slots.Where(slot => slot >= 0).Distinct().OrderBy(slot => slot).ToList();
         var parts = new List<string>();
         for (int i = 0; i < sorted.Count; i++) {
            int end = i;
            while (end + 1 < sorted.Count && sorted[end + 1] == sorted[end] + 1) end++;
            parts.Add(end > i ? $"{sorted[i]}-{sorted[end]}" : sorted[i].ToString());
            i = end;
         }
         return string.Join(", ", parts);
      }

      /// <summary>"palette slots 1-4 are painted with the skin tone, slots 10-11 with the clothes colour"</summary>
      public string Describe() {
         var skin = FormatSlots(Skin);
         var clothes = FormatSlots(Clothes);
         if (skin.Length == 0 && clothes.Length == 0) return "no palette slots are painted";
         if (skin.Length == 0) return $"{Slots(clothes, "palette ")} {Verb(clothes)} painted with the clothes colour";
         if (clothes.Length == 0) return $"{Slots(skin, "palette ")} {Verb(skin)} painted with the skin tone";
         return $"{Slots(skin, "palette ")} {Verb(skin)} painted with the skin tone, {Slots(clothes, "")} with the clothes colour";
      }

      /// <summary>"Skin: main slot 2, shadow slot 3, highlight slot 1, outline slot 4 (made from the shadow). Clothes: main slot 10, shadow slot 11." Only the slots that are used.</summary>
      public string DescribeSlots() {
         string List(IReadOnlyList<int> slots, string[] names) {
            var parts = new List<string>();
            for (int i = 0; i < slots.Count && i < names.Length; i++) if (slots[i] >= 0) parts.Add($"{names[i]} slot {slots[i]}" + (names[i] == "outline" ? " (made from the shadow)" : string.Empty));
            return string.Join(", ", parts);
         }
         var skin = List(Skin, new[] { "main", "shadow", "highlight", "outline" });
         var clothes = List(Clothes, new[] { "main", "shadow", "highlight" });
         var sentences = new List<string>();
         if (skin.Length > 0) sentences.Add($"Skin: {skin}.");
         if (clothes.Length > 0) sentences.Add($"Clothes: {clothes}.");
         return sentences.Count == 0 ? "No palette slot is painted." : string.Join(" ", sentences);
      }

      private static bool IsMany(string slots) => slots.Contains('-') || slots.Contains(',');
      private static string Slots(string slots, string prefix) => prefix + (IsMany(slots) ? "slots " : "slot ") + slots;
      private static string Verb(string slots) => IsMany(slots) ? "are" : "is";
   }

   public enum CharacterColorKind { SkinTone, Clothes }

   /// <summary>The outcome of changing a table: what to tell the person, and for adding, which row it became.</summary>
   public class CharacterEditResult {
      public bool Success { get; }
      public string Message { get; }
      public int Index { get; }
      public CharacterEditResult(bool success, string message, int index = -1) => (Success, Message, Index) = (success, message, index);
      public static CharacterEditResult Ok(int index = -1, string message = null) => new(true, message ?? string.Empty, index);
      public static CharacterEditResult Fail(string message) => new(false, message);
   }

   /// <summary>
   /// One of the two colour tables of the character customization: the skin tones or the clothes colours.
   /// A row is a 16-byte game-text name and three GBA colours (main, shadow, highlight). Row 0 is "Original": the game does not recolour then.
   /// The game takes the number of rows from a counter byte of its own, so every change that adds or removes a row updates the counter too.
   /// Everything is found through the metadata's anchor names, never through addresses: the tables move when they grow.
   /// </summary>
   public class CharacterColorTable {
      public const int NameBytes = 16, MaxNameLength = 15, ColorsPerRow = 3, ColorOffset = 16, MinRowLength = 22, MaxRows = 255;

      private readonly IDataModel model;
      private readonly Func<ModelDelta> tokenFactory;

      public CharacterColorKind Kind { get; }
      public string TableName { get; }
      public string CountName { get; }

      public CharacterColorTable(IDataModel model, Func<ModelDelta> tokenFactory, CharacterColorKind kind) {
         this.model = model;
         this.tokenFactory = tokenFactory;
         Kind = kind;
         TableName = kind == CharacterColorKind.SkinTone ? CharacterCustomization.SkinTonesAnchor : CharacterCustomization.ClothesAnchor;
         CountName = kind == CharacterColorKind.SkinTone ? CharacterCustomization.SkinToneCountAnchor : CharacterCustomization.ClothesCountAnchor;
      }

      public string Noun => Kind == CharacterColorKind.SkinTone ? "skin tone" : "clothes colour";
      public string NounPlural => Kind == CharacterColorKind.SkinTone ? "skin tones" : "clothes colours";

      #region Finding the data

      public int TableAddress => model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, TableName);
      public int CountAddress => model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, CountName);

      /// <summary>The table as the metadata describes it right now (null if the ROM has none). It has to be asked for again after every change: it moves when it grows.</summary>
      public ITableRun Run {
         get {
            var address = TableAddress;
            if (address < 0 || address >= model.Count) return null;
            var run = model.GetNextRun(address) as ITableRun;
            return run != null && run.Start == address ? run : null;
         }
      }

      public bool Exists => Run != null && CountAddress >= 0 && CountAddress < model.Count;

      /// <summary>The number the game uses as the number of rows.</summary>
      public int StoredCount => Exists ? model[CountAddress] : 0;

      /// <summary>The number of rows the metadata says the table has.</summary>
      public int TableRows => Run?.ElementCount ?? 0;

      /// <summary>
      /// Null if the counter and the table agree and are sane. Otherwise a friendly sentence: what is wrong, and that <see cref="FixCount"/> repairs it.
      /// While there is a problem rows can still be recoloured and renamed, but not added, removed or moved.
      /// </summary>
      public string Problem {
         get {
            var run = Run;
            if (run == null || CountAddress < 0 || CountAddress >= model.Count) return $"This ROM has no {Noun} table ({TableName}) in its metadata.";
            if (run.ElementLength < MinRowLength) return $"The {Noun} table's rows are {run.ElementLength} bytes long, but a row needs {MinRowLength} (a 16 byte name and three colours).";
            var count = model[CountAddress];
            if (run.ElementCount < 1) return $"The {Noun} table has no rows.";
            if (count == 0) return $"The counter in the ROM says there are 0 {NounPlural}. The game needs at least row 0 (Original). Press Fix to make the counter match the table's {Math.Min(run.ElementCount, MaxRows)} rows.";
            if (run.ElementCount > MaxRows) return $"The {Noun} table has {run.ElementCount} rows, but the game can only use {MaxRows}. Press Fix to cut the table to {MaxRows} rows.";
            if (count > run.ElementCount) return $"The counter in the ROM says there are {count} {NounPlural}, but the table only has {run.ElementCount} rows. Press Fix to make the counter match the table.";
            if (count < run.ElementCount) return $"The table has {run.ElementCount} rows, but the counter in the ROM says {count}, so the game ignores the last {run.ElementCount - count}. Press Fix to make the counter match the table.";
            return null;
         }
      }

      public bool IsHealthy => Problem == null;

      /// <summary>How many rows can be read safely: what the counter says, but never more than the table holds or the game can use.</summary>
      public int VisibleRows {
         get {
            var run = Run;
            if (run == null || run.ElementLength < MinRowLength) return 0;
            var count = CountAddress >= 0 && CountAddress < model.Count ? model[CountAddress] : 0;
            if (count < 1) count = run.ElementCount;
            return Math.Min(Math.Min(count, run.ElementCount), MaxRows);
         }
      }

      public int RowAddress(int index) {
         var run = Run;
         return run == null ? -1 : run.Start + run.ElementLength * index;
      }

      #endregion

      #region Reading

      public IReadOnlyList<CharacterColorRow> ReadRows() {
         var rows = new List<CharacterColorRow>();
         var count = VisibleRows;
         for (int i = 0; i < count; i++) rows.Add(ReadRow(i));
         return rows;
      }

      public CharacterColorRow ReadRow(int index) {
         var address = RowAddress(index);
         var name = model.TextConverter.Convert(model, address, NameBytes).Trim('"');
         var colors = new ushort[ColorsPerRow];
         for (int i = 0; i < colors.Length; i++) colors[i] = (ushort)model.ReadMultiByteValue(address + ColorOffset + 2 * i, 2);
         return new CharacterColorRow(index, name, colors);
      }

      #endregion

      #region Editing one row

      /// <summary>
      /// Turns text into the 16 bytes of a name: game text, at most 15 characters, ended by 0xFF and padded with 0xFF.
      /// </summary>
      public static bool TryEncodeName(IDataModel model, string text, out byte[] field, out string error) {
         field = null;
         text = (text ?? string.Empty).Trim();
         if (text.Length == 0) { error = "A name can't be empty."; return false; }
         var bytes = model.TextConverter.Convert(text, out var containsBadCharacters);
         while (bytes.Count > 0 && bytes[bytes.Count - 1] == 0xFF) bytes.RemoveAt(bytes.Count - 1);
         if (containsBadCharacters || bytes.Any(b => b >= 0xF7 && b != 0xFF)) { error = "The game's text can't show some of those characters (no line breaks or special codes in names)."; return false; }
         if (bytes.Count == 0) { error = "A name can't be empty."; return false; }
         if (bytes.Count > MaxNameLength) { error = $"Names are at most {MaxNameLength} characters long so they fit in the game's menu."; return false; }
         field = new byte[NameBytes];
         for (int i = 0; i < field.Length; i++) field[i] = i < bytes.Count ? bytes[i] : (byte)0xFF;
         error = null;
         return true;
      }

      public CharacterEditResult SetName(int index, string text) {
         if (index < 0 || index >= VisibleRows) return CharacterEditResult.Fail("That row doesn't exist.");
         if (index == 0) return CharacterEditResult.Fail("Row 0 is the original look and keeps its name.");
         if (!TryEncodeName(model, text, out var field, out var error)) return CharacterEditResult.Fail(error);
         tokenFactory().ChangeData(model, RowAddress(index), field);
         return CharacterEditResult.Ok(index);
      }

      public CharacterEditResult SetColor(int index, int slot, ushort color) {
         if (index < 0 || index >= VisibleRows) return CharacterEditResult.Fail("That row doesn't exist.");
         if (index == 0) return CharacterEditResult.Fail("Row 0 is the original look: the game does not recolour it, so its colours can't be edited.");
         if (slot < 0 || slot >= ColorsPerRow) return CharacterEditResult.Fail("A row has three colours: main, shadow and highlight.");
         model.WriteMultiByteValue(RowAddress(index) + ColorOffset + 2 * slot, 2, tokenFactory, color & 0x7FFF); // only asks for a change if the colour really differs
         return CharacterEditResult.Ok(index);
      }

      public CharacterEditResult SetColors(int index, IReadOnlyList<ushort> colors) {
         for (int slot = 0; slot < ColorsPerRow && slot < colors.Count; slot++) {
            var result = SetColor(index, slot, colors[slot]);
            if (!result.Success) return result;
         }
         return CharacterEditResult.Ok(index);
      }

      /// <summary>Make the shadow and the highlight from the main colour.</summary>
      public CharacterEditResult DeriveShades(int index) {
         if (index < 0 || index >= VisibleRows) return CharacterEditResult.Fail("That row doesn't exist.");
         var row = ReadRow(index);
         var (shadow, highlight) = GbaColor.DeriveShades(row.Colors[0]);
         return SetColors(index, new[] { row.Colors[0], shadow, highlight });
      }

      #endregion

      #region Adding, removing, moving

      private CharacterEditResult CheckHealthy() {
         var problem = Problem;
         return problem == null ? null : CharacterEditResult.Fail(problem);
      }

      /// <summary>
      /// Adds a row at the end of the table. If the table has no room it moves to free space (the references to it are updated),
      /// and the counter the game reads is raised by one: all in the current change, so one undo takes it all back.
      /// </summary>
      public CharacterEditResult Add(string name, IReadOnlyList<ushort> colors) {
         var unhealthy = CheckHealthy();
         if (unhealthy != null) return unhealthy;
         if (!TryEncodeName(model, name, out var field, out var error)) return CharacterEditResult.Fail(error);
         var run = Run;
         var count = run.ElementCount;
         if (count >= MaxRows) return CharacterEditResult.Fail($"The game can handle at most {MaxRows} {NounPlural}.");
         if (!run.CanAppend) return CharacterEditResult.Fail($"The {Noun} table can't grow.");

         var token = tokenFactory();
         var start = run.Start;
         var grown = model.RelocateForExpansion(token, run, run.Length + run.ElementLength);
         grown = grown.Append(token, 1);
         model.ObserveRunWritten(token, grown);

         var address = grown.Start + grown.ElementLength * count;
         for (int i = 0; i < grown.ElementLength; i++) token.ChangeData(model, address + i, 0);
         token.ChangeData(model, address, field);
         for (int i = 0; i < ColorsPerRow; i++) model.WriteMultiByteValue(address + ColorOffset + 2 * i, 2, token, (i < colors.Count ? colors[i] : 0) & 0x7FFF);
         model.WriteMultiByteValue(CountAddress, 1, token, count + 1);
         var moved = grown.Start != start;
         return CharacterEditResult.Ok(count, moved ? $"The {Noun} table was moved to free space to make room. The game's references to it were updated." : null);
      }

      /// <summary>A copy of a row, at the end of the table.</summary>
      public CharacterEditResult Duplicate(int index) {
         if (index < 0 || index >= VisibleRows) return CharacterEditResult.Fail("That row doesn't exist.");
         var row = ReadRow(index);
         return Add(UniqueName(row.Name), row.Colors);
      }

      /// <summary>The name with " 2", " 3"... added until no row has it, cut short if needed so it still fits.</summary>
      public string UniqueName(string baseName) {
         var existing = new HashSet<string>(ReadRows().Select(row => row.Name));
         baseName = (baseName ?? string.Empty).Trim();
         if (baseName.Length == 0) baseName = Kind == CharacterColorKind.SkinTone ? "New skin tone" : "New colour";
         if (baseName.Length > MaxNameLength) baseName = baseName.Substring(0, MaxNameLength).TrimEnd();
         if (!existing.Contains(baseName)) return baseName;
         for (int n = 2; n < 100; n++) {
            var suffix = " " + n;
            var stem = baseName.Length + suffix.Length > MaxNameLength ? baseName.Substring(0, MaxNameLength - suffix.Length).TrimEnd() : baseName;
            if (!existing.Contains(stem + suffix)) return stem + suffix;
         }
         return baseName;
      }

      /// <summary>
      /// Removes a row (never row 0). Rows after it move up by one and the table gets shorter.
      /// A saved game that picked one of the rows after it ends up with the row that took its place.
      /// </summary>
      public CharacterEditResult Remove(int index) {
         var unhealthy = CheckHealthy();
         if (unhealthy != null) return unhealthy;
         var run = Run;
         var count = run.ElementCount;
         if (index <= 0) return CharacterEditResult.Fail("Row 0 is the original look and can't be removed.");
         if (index >= count) return CharacterEditResult.Fail("That row doesn't exist.");

         var token = tokenFactory();
         var length = run.ElementLength;
         for (int row = index; row < count - 1; row++) {
            var to = run.Start + length * row;
            var from = to + length;
            for (int i = 0; i < length; i++) token.ChangeData(model, to + i, model[from + i]);
         }
         var last = run.Start + length * (count - 1);
         for (int i = 0; i < length; i++) token.ChangeData(model, last + i, 0xFF);
         var shorter = run.Append(token, -1);
         model.ObserveRunWritten(token, shorter);
         model.WriteMultiByteValue(CountAddress, 1, token, count - 1);
         return CharacterEditResult.Ok(Math.Min(index, count - 2), index < count - 1 ? $"The {NounPlural} after it moved up by one. A saved game that had picked one of them now shows its neighbour. The game shows the original look for a number that no longer exists." : null);
      }

      /// <summary>Swaps a row with the one above (direction -1) or below (direction 1). Row 0 never moves.</summary>
      public CharacterEditResult Move(int index, int direction) {
         var unhealthy = CheckHealthy();
         if (unhealthy != null) return unhealthy;
         var run = Run;
         var other = index + direction;
         if (index <= 0 || other <= 0 || index >= run.ElementCount || other >= run.ElementCount) return CharacterEditResult.Fail("That row can't move there (row 0 stays first).");

         var token = tokenFactory();
         var length = run.ElementLength;
         var a = run.Start + length * index;
         var b = run.Start + length * other;
         for (int i = 0; i < length; i++) {
            var byteA = model[a + i];
            var byteB = model[b + i];
            token.ChangeData(model, a + i, byteB);
            token.ChangeData(model, b + i, byteA);
         }
         return CharacterEditResult.Ok(other);
      }

      /// <summary>Makes the counter the game reads match the table (at most 255 rows, what the counter byte can hold: a longer table is cut to that).</summary>
      public CharacterEditResult FixCount() {
         var run = Run;
         if (run == null || CountAddress < 0) return CharacterEditResult.Fail(Problem);
         if (run.ElementLength < MinRowLength) return CharacterEditResult.Fail(Problem);
         if (run.ElementCount < 1) return CharacterEditResult.Fail(Problem);
         var token = tokenFactory();
         var rows = run.ElementCount;
         if (rows > MaxRows) {
            var shorter = run.Append(token, MaxRows - rows);
            model.ObserveRunWritten(token, shorter);
            rows = MaxRows;
         }
         model.WriteMultiByteValue(CountAddress, 1, token, rows);
         return CharacterEditResult.Ok(rows - 1);
      }

      #endregion
   }

   /// <summary>
   /// Character customization: the player can choose a skin tone and a clothes colour, and the game paints them over chosen slots of the
   /// boy's and the girl's palettes. The ROM keeps two tables of colours, a table that says which palette slots get painted, and a counter for each.
   /// See the "characterCustomization.*" anchors.
   /// </summary>
   public class CharacterCustomization {
      public const string SkinTonesAnchor = "characterCustomization.skinTones";
      public const string ClothesAnchor = "characterCustomization.clothes";
      public const string RolesAnchor = "characterCustomization.roles";
      public const string SkinToneCountAnchor = "characterCustomization.skinToneCount";
      public const string ClothesCountAnchor = "characterCustomization.clothesCount";
      public const string RoleCountAnchor = "characterCustomization.roleCount";

      /// <summary>A role is 9 bytes: gender, context, four skin slots (main, shadow, highlight, outline), three clothes slots (0xFF = not used).</summary>
      public const int RoleLength = 9;

      private readonly IDataModel model;

      public CharacterColorTable SkinTones { get; }
      public CharacterColorTable Clothes { get; }

      public CharacterCustomization(IDataModel model, Func<ModelDelta> tokenFactory) {
         this.model = model;
         SkinTones = new CharacterColorTable(model, tokenFactory, CharacterColorKind.SkinTone);
         Clothes = new CharacterColorTable(model, tokenFactory, CharacterColorKind.Clothes);
      }

      /// <summary>True if the metadata describes both colour tables, the roles and the counters.</summary>
      public static bool IsSupported(IDataModel model) {
         if (model == null) return false;
         foreach (var name in new[] { SkinToneCountAnchor, ClothesCountAnchor, RoleCountAnchor }) {
            var address = model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, name);
            if (address < 0 || address >= model.Count) return false;
         }
         foreach (var name in new[] { SkinTonesAnchor, ClothesAnchor, RolesAnchor }) {
            var address = model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, name);
            if (address < 0 || address >= model.Count) return false;
            if (model.GetNextRun(address) is not ITableRun run || run.Start != address) return false;
         }
         return true;
      }

      public ITableRun RoleRun {
         get {
            var address = model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, RolesAnchor);
            if (address < 0 || address >= model.Count) return null;
            var run = model.GetNextRun(address) as ITableRun;
            return run != null && run.Start == address ? run : null;
         }
      }

      /// <summary>The roles the ROM lists: for every kind of picture of the boy and the girl, which palette slots get the colours.</summary>
      public IReadOnlyList<CharacterRole> ReadRoles() {
         var roles = new List<CharacterRole>();
         var run = RoleRun;
         var countAddress = model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, RoleCountAnchor);
         if (run == null || run.ElementLength < RoleLength || countAddress < 0 || countAddress >= model.Count) return roles;
         var count = Math.Min(model[countAddress], run.ElementCount);
         for (int i = 0; i < count; i++) {
            var start = run.Start + run.ElementLength * i;
            int Slot(int offset) => model[start + offset] < 16 ? model[start + offset] : -1;
            roles.Add(new CharacterRole(model[start], model[start + 1], new[] { Slot(2), Slot(3), Slot(4), Slot(5) }, new[] { Slot(6), Slot(7), Slot(8) }));
         }
         return roles;
      }

      public CharacterRole FindRole(int gender, int context) => ReadRoles().FirstOrDefault(role => role.Gender == gender && role.Context == context);
   }
}
