using HavenSoft.HexManiac.Core.Models.Code;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.Models.Runs.Sprites;
using HexManiac.Core.Models.Runs.Sprites;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HavenSoft.HexManiac.Core.Models.Map {
   /// <summary>
   /// The RAM/ROM locations the tileset animation engine uses. They come from the ROM's metadata ([[UnmappedConstant]] entries),
   /// because they differ between games and between decomp builds.
   /// </summary>
   public record TilesetAnimationConstants(int Buffer, int BufferSize, int PrimaryCounter, int PrimaryMax, int PrimaryCallback, int SecondaryCounter, int SecondaryMax, int SecondaryCallback, int PrimaryTiles) {
      public const string Prefix = "tilesetanim.";

      public static bool TryRead(IDataModel model, out TilesetAnimationConstants constants) {
         constants = null;
         int Get(string name) => model.TryGetUnmappedConstant(Prefix + name, out var value) ? value : -1;
         var buffer = Get("buffer");
         var bufferSize = Get("buffersize");
         var pCounter = Get("primary.counter");
         var pMax = Get("primary.max");
         var pCallback = Get("primary.callback");
         var sCounter = Get("secondary.counter");
         var sMax = Get("secondary.max");
         var sCallback = Get("secondary.callback");
         var primaryTiles = Get("primarytiles");
         if (primaryTiles < 0) primaryTiles = model.IsFRLG() ? 640 : 512;
         if (new[] { buffer, bufferSize, pCounter, pMax, pCallback, sCounter, sMax, sCallback }.Any(value => value < 0)) return false;
         constants = new(buffer, bufferSize, pCounter, pMax, pCallback, sCounter, sMax, sCallback, primaryTiles);
         return true;
      }
   }

   /// <summary>
   /// One animated group of tiles: 'tiles' consecutive tiles starting at 'tileOffset' are replaced every (1 shl timer) frames with the next frame's graphics.
   /// </summary>
   public class TilesetAnimationEntry {
      public int Index { get; init; }
      public int FirstTile { get; init; }      // relative to the tileset (0..511 for a primary tileset)
      public int TileCount { get; init; }
      public int FrameCount { get; init; }
      public int Timer { get; init; }          // speed: the frame changes every 2^Timer game frames
      public int FramesAddress { get; init; }  // the `mat` run (table of frame pointers)
      public int EntryAddress { get; init; }
      public int FrameDelay => 1 << Timer;
      /// <summary>True for the game's own (code driven) animations: water, flowers... Their speed and frame count are fixed by the game code, but their frames can be edited.</summary>
      public bool IsBuiltIn { get; init; }
      public string Name { get; init; }
      /// <summary>How many frames this animation is ahead by (the second steam plume of Lavaridge runs 2 frames ahead of the first).</summary>
      public int Phase { get; init; }
   }

   /// <summary>
   /// Installs and edits table-driven tileset animations in a pokeemerald/pokeemerald-expansion ROM.
   /// The table format is the same one HexManiacAdvance uses for FireRed: [animations{mat} frames: timer. tiles. tileOffset::]!FEFEFEFE,
   /// and a small thumb routine (written into free space) plays it. The tileset's own animation (flowers, water...) keeps running:
   /// the generated code calls the original animation first.
   /// </summary>
   public class TilesetAnimations {
      public const string AnchorPrefix = "graphics.maps.tilesets.animations.";
      public const string TableFormat = "[animations<`mat`> frames: timer. tiles. tileOffset::]!FEFEFEFE";
      public const int EntryLength = 12;
      public const int MaxTimer = 7;

      private readonly IDataModel model;
      private readonly Func<ModelDelta> tokenFactory;
      private readonly ThumbParser parser;

      public TilesetAnimations(IDataModel model, Func<ModelDelta> tokenFactory, ThumbParser parser) {
         this.model = model;
         this.tokenFactory = tokenFactory;
         this.parser = parser;
      }

      public static bool IsSupported(IDataModel model) => TilesetAnimationConstants.TryRead(model, out _);

      /// <summary>
      /// The offset of the 'animation' (callback) pointer within a tileset header: isCompressed. isSecondary. (2 bytes) tiles palette blocks attributes animation
      /// </summary>
      public static int CallbackOffset(IDataModel model) => model.IsFRLG() ? 16 : 20;

      public int ReadCallback(int tilesetStart) => model.ReadPointer(tilesetStart + CallbackOffset(model));

      /// <summary>
      /// If this tileset already uses a HexManiac animation table, return it.
      /// </summary>
      public bool TryGetTable(int tilesetStart, out ITableRun table, out string baseName) {
         table = null;
         baseName = null;
         var callback = ReadCallback(tilesetStart);
         if (callback < 0 || callback >= model.Count) return false;
         var anchor = model.GetAnchorFromAddress(-1, callback);
         if (string.IsNullOrEmpty(anchor) || !anchor.StartsWith(AnchorPrefix) || !anchor.EndsWith(".init")) return false;
         baseName = anchor.Substring(0, anchor.Length - ".init".Length);
         var tableAddress = model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, baseName + ".table");
         if (tableAddress < 0) return false;
         table = model.GetNextRun(tableAddress) as ITableRun;
         return table != null && table.Start == tableAddress;
      }

      /// <summary>
      /// Describes what animates this tileset right now.
      /// </summary>
      public string DescribeCallback(int tilesetStart) {
         var callback = ReadCallback(tilesetStart);
         if (callback < 0 || callback >= model.Count) return "This tileset has no animation.";
         if (TryGetTable(tilesetStart, out var table, out _)) return $"{table.ElementCount} custom animation{(table.ElementCount == 1 ? "" : "s")} (HexManiac table).";
         var anchor = model.GetAnchorFromAddress(-1, callback);
         return string.IsNullOrEmpty(anchor) ? $"The game's own animation code at {callback:X6}. It keeps running after you add animations." : $"The game's own animation code ({anchor}). It keeps running after you add animations.";
      }

      public IReadOnlyList<TilesetAnimationEntry> ReadEntries(ITableRun table, bool isSecondary, TilesetAnimationConstants constants) {
         var results = new List<TilesetAnimationEntry>();
         if (table == null) return results;
         int tileBase = isSecondary ? constants.PrimaryTiles : 0;
         for (int i = 0; i < table.ElementCount; i++) {
            var entry = table.Start + table.ElementLength * i;
            results.Add(new TilesetAnimationEntry {
               Index = i,
               EntryAddress = entry,
               FramesAddress = model.ReadPointer(entry),
               FrameCount = model.ReadMultiByteValue(entry + 4, 2),
               Timer = model[entry + 6],
               TileCount = model[entry + 7],
               FirstTile = model.ReadMultiByteValue(entry + 8, 4) - tileBase,
            });
         }
         return results;
      }

      /// <summary>
      /// The table that describes the game's own tileset animations (written into the ROM by the build, see BuiltInTableName):
      /// tileset<> frames<> frameCount: firstTile: tileCount. timerShift. phase. padding. name""32
      /// </summary>
      public const string BuiltInTableName = "data.maps.tilesets.builtinanimations";
      private const int BuiltInNameOffset = 16, BuiltInNameLength = 32;

      public bool HasBuiltInTable => model.GetTable(BuiltInTableName) != null;

      /// <summary>
      /// The animations the game's own code plays for the tileset that starts at 'tilesetStart'. Empty when the ROM has no such table.
      /// </summary>
      public IReadOnlyList<TilesetAnimationEntry> ReadBuiltInEntries(int tilesetStart, bool isSecondary, TilesetAnimationConstants constants) {
         var results = new List<TilesetAnimationEntry>();
         var table = model.GetTable(BuiltInTableName);
         if (table == null || table.ElementLength < BuiltInNameOffset + 1) return results;
         int tileBase = isSecondary ? constants.PrimaryTiles : 0;
         for (int i = 0; i < table.ElementCount; i++) {
            var entry = table.Start + table.ElementLength * i;
            if (model.ReadPointer(entry) != tilesetStart) continue;
            var frames = model.ReadPointer(entry + 4);
            var frameCount = model.ReadMultiByteValue(entry + 8, 2);
            var tileCount = model[entry + 12];
            if (frames < 0 || frameCount < 1 || tileCount < 1) continue;
            results.Add(new TilesetAnimationEntry {
               IsBuiltIn = true,
               Index = i,
               EntryAddress = entry,
               FramesAddress = frames,
               FrameCount = frameCount,
               FirstTile = model.ReadMultiByteValue(entry + 10, 2) - tileBase,
               TileCount = tileCount,
               Timer = Math.Min(MaxTimer, (int)model[entry + 13]),
               Phase = model[entry + 14],
               Name = ReadName(entry + BuiltInNameOffset),
            });
         }
         return results;
      }

      private string ReadName(int start) {
         int length = 0;
         while (length < BuiltInNameLength && start + length < model.Count && model[start + length] != 0xFF) length++;
         if (length == 0) return string.Empty;
         return PCSString.Convert(model.RawData, start, length).Trim('"');
      }

      /// <summary>
      /// Read the raw 4bpp tile data (32 bytes per tile) of a frame. Returns null if the frame pointer is invalid.
      /// </summary>
      public byte[] ReadFrame(TilesetAnimationEntry entry, int frame) {
         if (entry.FramesAddress < 0 || entry.FramesAddress + 4 * entry.FrameCount > model.Count || frame >= entry.FrameCount) return null;
         var frameAddress = model.ReadPointer(entry.FramesAddress + 4 * frame);
         if (frameAddress < 0 || frameAddress + 32 * entry.TileCount > model.Count) return null;
         var data = new byte[32 * entry.TileCount];
         Array.Copy(model.RawData, frameAddress, data, 0, data.Length);
         return data;
      }

      public void WriteFrame(TilesetAnimationEntry entry, int frame, byte[] data) {
         if (entry.FramesAddress < 0 || frame >= entry.FrameCount) return;
         var frameAddress = model.ReadPointer(entry.FramesAddress + 4 * frame);
         if (frameAddress < 0 || frameAddress + data.Length > model.Count) return;
         var token = tokenFactory();
         for (int i = 0; i < data.Length; i++) {
            if (model[frameAddress + i] != data[i]) token.ChangeData(model, frameAddress + i, data[i]);
         }
      }

      /// <summary>
      /// Make sure the tileset runs a HexManiac animation table (creating the table and the code if needed), then return the table.
      /// </summary>
      public ITableRun EnsureTable(int tilesetStart, bool isSecondary, TilesetAnimationConstants constants, out string baseName) {
         if (TryGetTable(tilesetStart, out var existing, out baseName)) return existing;
         var token = tokenFactory();

         // the original animation, so it can keep running
         var originalInit = ReadCallback(tilesetStart);
         if (originalInit < 0 || originalInit >= model.Count) originalInit = Pointer.NULL;
         var originalCallback = originalInit == Pointer.NULL ? Pointer.NULL : FindCallbackInInit(originalInit);

         baseName = AnchorPrefix + $"t{tilesetStart:x6}";
         var tableName = baseName + ".table";
         var tableAddress = model.FindFreeSpace(model.FreeSpaceStart, 16);
         if (tableAddress < 0) { tableAddress = model.Count; model.ExpandData(token, model.Count + 0x100); }
         // an empty table: just the end token
         model.WriteMultiByteValue(tableAddress, 4, token, -1 - 0x01010101); // FEFEFEFE
         for (int i = 4; i < 12; i++) token.ChangeData(model, tableAddress + i, 0xFF);
         var error = ArrayRun.TryParse(model, TableFormat, tableAddress, SortedSpan<int>.None, out var tableRun);
         if (error.HasError) throw new InvalidOperationException(error.ErrorMessage);
         model.ObserveAnchorWritten(token, tableName, tableRun);
         EnsurePaletteAnchor(tilesetStart, baseName, token);

         var callbackAddress = InsertCallback(baseName + ".callback", tableAddress, originalCallback, constants);
         var initAddress = InsertInit(baseName + ".init", tableAddress, callbackAddress, originalInit, isSecondary, constants);

         // point the tileset at the new init routine (thumb: +1)
         var pointerAddress = tilesetStart + CallbackOffset(model);
         if (model.GetNextRun(tilesetStart) is ITableRun tilesetRun && tilesetRun.Start == tilesetStart && tilesetRun.ElementContent.Any(segment => segment.Name == "animation")) {
            new ModelArrayElement(model, tilesetRun.Start, 0, tokenFactory, tilesetRun).SetAddress("animation", initAddress + 1);
         } else {
            model.ClearFormat(token, pointerAddress, 4);
            model.WritePointer(token, pointerAddress, initAddress + 1);
            model.ObserveRunWritten(token, new PointerRun(pointerAddress));
         }
         return (ITableRun)model.GetNextRun(tableAddress);
      }

      /// <summary>
      /// Name the tileset's 16-palette table '<name>.palette' so the frames (uct4xN|<name>.palette) show in the right colours.
      /// </summary>
      public string EnsurePaletteAnchor(int tilesetStart, string baseName, ModelDelta token) {
         var paletteName = baseName + ".palette";
         if (model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, paletteName) >= 0) return paletteName;
         var paletteAddress = model.ReadPointer(tilesetStart + 8);
         if (paletteAddress < 0 || paletteAddress + 512 > model.Count) return null;
         var existing = model.GetNextRun(paletteAddress);
         if (existing is IPaletteRun paletteRun && paletteRun.Start == paletteAddress) {
            model.ObserveAnchorWritten(token, paletteName, paletteRun);
            return paletteName;
         }
         if (!PaletteRun.TryParsePaletteFormat("`ucp4:0123456789ABCDEF`", out var format)) return null;
         model.ClearFormat(token, paletteAddress, 512);
         model.ObserveAnchorWritten(token, paletteName, new PaletteRun(paletteAddress, format, SortedSpan.One(tilesetStart + 8)));
         return paletteName;
      }

      /// <summary>
      /// Make sure a frame is registered as tiles that know which palette they use, so the image editor shows them in colour.
      /// Returns the frame's address, or -1 if the frame pointer is bad.
      /// </summary>
      public int EnsureFrameFormat(int tilesetStart, TilesetAnimationEntry entry, int frame) {
         if (entry.FramesAddress < 0 || entry.FramesAddress + 4 * entry.FrameCount > model.Count || frame < 0 || frame >= entry.FrameCount) return -1;
         var frameAddress = model.ReadPointer(entry.FramesAddress + 4 * frame);
         var length = 32 * entry.TileCount;
         if (frameAddress < 0 || frameAddress + length > model.Count) return -1;
         // the tileset's palettes are named after the tileset, whether it uses a HexManiac table or only the game's own animations
         if (!TryGetTable(tilesetStart, out _, out var baseName)) baseName = AnchorPrefix + $"t{tilesetStart:x6}";
         var token = tokenFactory();
         var hint = EnsurePaletteAnchor(tilesetStart, baseName, token);
         if (hint == null) return frameAddress;
         var existing = model.GetNextRun(frameAddress);
         if (existing is ISpriteRun sprite && sprite.Start == frameAddress && sprite.SpriteFormat.PaletteHint == hint && sprite.PointerSources.Count > 0) return frameAddress;
         var sources = existing.Start == frameAddress && existing.PointerSources != null ? existing.PointerSources : SortedSpan<int>.None;
         // every slot of the frame table that points at this frame (the flower animation reuses its first frame)
         for (int f = 0; f < entry.FrameCount; f++) {
            if (model.ReadPointer(entry.FramesAddress + 4 * f) == frameAddress) sources = sources.Add1(entry.FramesAddress + 4 * f);
         }
         if (existing.Start != frameAddress) model.ClearFormat(token, frameAddress, length);
         model.ObserveRunWritten(token, new TilesetRun(new TilesetFormat(4, entry.TileCount, -1, hint), model, frameAddress, sources));
         return frameAddress;
      }

      private string PaletteHintFor(ITableRun table) {
         var anchor = model.GetAnchorFromAddress(-1, table.Start);
         if (string.IsNullOrEmpty(anchor) || !anchor.EndsWith(".table")) return null;
         var paletteName = anchor.Substring(0, anchor.Length - ".table".Length) + ".palette";
         return model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, paletteName) >= 0 ? paletteName : null;
      }

      /// <summary>
      /// Add an animation entry whose frames all start as copies of the tileset's current tiles.
      /// </summary>
      public TilesetAnimationEntry AddEntry(ITableRun table, int firstTile, int tileCount, int frameCount, int timer, bool isSecondary, TilesetAnimationConstants constants, byte[] currentTiles) {
         var token = tokenFactory();
         tileCount = tileCount.LimitToRange(1, 255);
         frameCount = frameCount.LimitToRange(1, 64);
         timer = timer.LimitToRange(0, MaxTimer);
         var paletteHint = PaletteHintFor(table);
         var index = table.ElementCount;
         table = model.RelocateForExpansion(token, table, table.Length + table.ElementLength);
         table = table.Append(token, 1);
         model.ObserveRunWritten(token, table);
         var entry = table.Start + table.ElementLength * index;

         // the frame pointer table first: its pointers are written (and registered) before anything else is allocated,
         // because free space is found by looking for FF bytes, and a frame full of color 15 is all FF.
         var frameSize = 32 * tileCount;
         var framesAddress = model.FindFreeSpace(model.FreeSpaceStart, 4 * frameCount);
         if (framesAddress < 0) { framesAddress = model.Count; model.ExpandData(token, model.Count + 4 * frameCount + 0x10); }
         for (int f = 0; f < frameCount; f++) model.WritePointer(token, framesAddress + 4 * f, 0);
         model.ObserveRunWritten(token, new NoInfoRun(framesAddress, SortedSpan.One(entry)));
         for (int f = 0; f < frameCount; f++) {
            var frameAddress = model.FindFreeSpace(model.FreeSpaceStart, frameSize);
            if (frameAddress < 0) { frameAddress = model.Count; model.ExpandData(token, model.Count + frameSize + 0x10); }
            for (int i = 0; i < frameSize; i++) {
               int source = firstTile * 32 + i;
               byte value = currentTiles != null && source < currentTiles.Length ? currentTiles[source] : (byte)0;
               token.ChangeData(model, frameAddress + i, value);
            }
            model.WritePointer(token, framesAddress + 4 * f, frameAddress);
            model.ObserveRunWritten(token, new TilesetRun(new TilesetFormat(4, tileCount, -1, paletteHint), model, frameAddress, SortedSpan.One(framesAddress + 4 * f)));
         }

         // the entry itself (write the pointer last so the `mat` run can read frames/tiles from its parent)
         model.WriteMultiByteValue(entry + 4, 2, token, frameCount);
         model.WriteMultiByteValue(entry + 6, 1, token, timer);
         model.WriteMultiByteValue(entry + 7, 1, token, tileCount);
         model.WriteMultiByteValue(entry + 8, 4, token, firstTile + (isSecondary ? constants.PrimaryTiles : 0));
         model.ClearFormat(token, framesAddress, 4 * frameCount);
         var element = new ModelArrayElement(model, table.Start, index, tokenFactory, table);
         element.SetAddress("animations", framesAddress);
         for (int f = 0; f < frameCount; f++) {
            var frameAddress = model.ReadPointer(framesAddress + 4 * f);
            if (model.GetNextRun(frameAddress) is TilesetRun) continue;
            model.ObserveRunWritten(token, new TilesetRun(new TilesetFormat(4, tileCount, -1, paletteHint), model, frameAddress, SortedSpan.One(framesAddress + 4 * f)));
         }
         return ReadEntries((ITableRun)model.GetNextRun(table.Start), isSecondary, constants)[index];
      }

      public void RemoveEntry(ITableRun table, int index) {
         var token = tokenFactory();
         if (index < 0 || index >= table.ElementCount) return;
         // clear the frame graphics and the frame table
         var entry = table.Start + table.ElementLength * index;
         var framesAddress = model.ReadPointer(entry);
         var frameCount = model.ReadMultiByteValue(entry + 4, 2);
         var tileCount = model[entry + 7];
         if (framesAddress >= 0 && framesAddress + 4 * frameCount <= model.Count) {
            for (int f = 0; f < frameCount; f++) {
               var frameAddress = model.ReadPointer(framesAddress + 4 * f);
               if (frameAddress >= 0 && frameAddress + 32 * tileCount <= model.Count) {
                  model.ClearPointer(token, framesAddress + 4 * f, frameAddress);
                  model.ClearFormatAndData(token, frameAddress, 32 * tileCount);
               }
            }
            model.ClearPointer(token, entry, framesAddress);
            model.ClearFormatAndData(token, framesAddress, 4 * frameCount);
         }
         // shift later entries down
         for (int i = index + 1; i < table.ElementCount; i++) {
            var from = table.Start + table.ElementLength * i;
            var to = from - table.ElementLength;
            var destination = model.ReadPointer(from);
            model.ClearPointer(token, from, destination);
            for (int b = 4; b < table.ElementLength; b++) token.ChangeData(model, to + b, model[from + b]);
            model.WritePointer(token, to, destination);
         }
         var shorter = table.Append(token, -1);
         model.ObserveRunWritten(token, shorter);
      }

      public void SetTimer(ITableRun table, int index, int timer) {
         var entry = table.Start + table.ElementLength * index;
         model.WriteMultiByteValue(entry + 6, 1, tokenFactory(), timer.LimitToRange(0, MaxTimer));
      }

      /// <summary>
      /// Change the number of frames: new frames copy the last frame; removed frames are freed.
      /// </summary>
      public void SetFrameCount(ITableRun table, int index, int newCount) {
         newCount = newCount.LimitToRange(1, 64);
         var token = tokenFactory();
         var entry = table.Start + table.ElementLength * index;
         var oldCount = model.ReadMultiByteValue(entry + 4, 2);
         if (oldCount == newCount) return;
         var tileCount = model[entry + 7];
         var framesAddress = model.ReadPointer(entry);
         if (framesAddress < 0) return;
         var frames = (ITableRun)model.GetNextRun(framesAddress);
         if (newCount < oldCount) {
            for (int f = newCount; f < oldCount; f++) {
               var frameAddress = model.ReadPointer(framesAddress + 4 * f);
               if (frameAddress >= 0 && frameAddress + 32 * tileCount <= model.Count) {
                  model.ClearPointer(token, framesAddress + 4 * f, frameAddress);
                  model.ClearFormatAndData(token, frameAddress, 32 * tileCount);
               }
            }
            model.WriteMultiByteValue(entry + 4, 2, token, newCount);
            if (frames is IUpdateFromParentRun updatable) model.ObserveRunWritten(token, (IFormattedRun)updatable.UpdateFromParent(token, 1, entry));
         } else {
            var last = ReadFrameBytes(framesAddress, oldCount - 1, tileCount);
            model.WriteMultiByteValue(entry + 4, 2, token, newCount);
            if (frames is IUpdateFromParentRun updatable) frames = (ITableRun)updatable.UpdateFromParent(token, 1, entry);
            model.ObserveRunWritten(token, frames);
            framesAddress = frames.Start;
            for (int f = oldCount; f < newCount; f++) {
               var frameAddress = model.FindFreeSpace(model.FreeSpaceStart, 32 * tileCount);
               if (frameAddress < 0) { frameAddress = model.Count; model.ExpandData(token, model.Count + 32 * tileCount + 0x10); }
               for (int i = 0; i < 32 * tileCount; i++) token.ChangeData(model, frameAddress + i, last != null && i < last.Length ? last[i] : (byte)0);
               model.WritePointer(token, framesAddress + 4 * f, frameAddress);
               model.ObserveRunWritten(token, new TilesetRun(new TilesetFormat(4, tileCount, -1, PaletteHintFor(table)), model, frameAddress, SortedSpan.One(framesAddress + 4 * f)));
            }
         }
      }

      private byte[] ReadFrameBytes(int framesAddress, int frame, int tileCount) {
         var frameAddress = model.ReadPointer(framesAddress + 4 * frame);
         if (frameAddress < 0 || frameAddress + 32 * tileCount > model.Count) return null;
         var data = new byte[32 * tileCount];
         Array.Copy(model.RawData, frameAddress, data, 0, data.Length);
         return data;
      }

      /// <summary>
      /// The game's InitTilesetAnim_* routines are tiny: they store a counter max and a callback pointer that sits in their literal pool.
      /// Find that pointer (a thumb address in the ROM) so the generated code can keep calling it.
      /// </summary>
      private int FindCallbackInInit(int initAddress) {
         int start = initAddress & ~1;
         for (int offset = 0; offset < 0x40 && start + offset + 4 <= model.Count; offset += 4) {
            var word = model.ReadMultiByteValue(start + offset, 4);
            if (word < BaseModel.PointerOffset || word >= BaseModel.PointerOffset + model.Count) continue;
            if ((word & 1) == 0) continue;
            var candidate = word - BaseModel.PointerOffset;
            if (candidate == start + 1) continue;
            return candidate;
         }
         return Pointer.NULL;
      }

      private int InsertCallback(string name, int tableAddress, int originalCallback, TilesetAnimationConstants constants) {
         var token = tokenFactory();
         var start = model.FindFreeSpace(model.FreeSpaceStart, 0xC0);
         if (start < 0) { start = model.Count; model.ExpandData(token, model.Count + 0x100); }
         var chain = originalCallback == Pointer.NULL ? 0 : (originalCallback & ~1) + BaseModel.PointerOffset;
         var code = $@"
push {{r4-r7, lr}}
mov  r6, r0                     @ r6 = timer

@ keep the tileset's own animation running
ldr  r7, =0x{chain:X8}
cmp  r7, #0
beq  <read_table>
mov  r0, r6
bl   <long_branch>

read_table:
ldr  r4, =<{tableAddress:X6}>   @ r4 = current table entry
sub  r4, #12
ldr  r5, =0xFEFEFEFE            @ r5 = end token
loop:
  add  r4, #12
  ldr  r0, [r4, #0]
  cmp  r0, r5
  beq  <end>

  @ only animate when the timer is a multiple of 1 << entry.timer
  mov  r0, #1
  ldrb r1, [r4, #6]
  lsl  r0, r1
  sub  r0, #1
  and  r0, r6
  cmp  r0, #0
  bne  <loop>

  @ frame = (timer >> entry.timer) % entry.frames
  mov  r0, r6
  lsr  r0, r1
  ldrh r1, [r4, #4]
  cmp  r1, #0
  beq  <loop>
mod_loop:
  cmp  r0, r1
  blo  <mod_done>
  sub  r0, r0, r1
  b    <mod_loop>
mod_done:
  lsl  r0, #2
  ldr  r2, [r4, #0]
  ldr  r0, [r2, r0]             @ r0 = source graphics (this frame)

  @ r1 = destination in VRAM
  ldr  r1, [r4, #8]
  lsl  r1, #5
  ldr  r2, =0x06000000
  add  r1, r2

  @ r2 = byte count
  ldrb r2, [r4, #7]
  lsl  r2, #5

  @ queue the copy: if (bufferSize < 20) buffer[bufferSize++] = (src, dest, size)
  ldr  r3, =0x{constants.BufferSize:X8}
  ldrb r7, [r3, #0]
  cmp  r7, #20
  bhs  <loop>
  lsl  r5, r7, #3
  lsl  r7, r7, #2
  add  r7, r5
  ldr  r5, =0x{constants.Buffer:X8}
  add  r7, r5
  str  r0, [r7, #0]
  str  r1, [r7, #4]
  strh r2, [r7, #8]
  ldrb r7, [r3, #0]
  add  r7, #1
  strb r7, [r3, #0]
  ldr  r5, =0xFEFEFEFE
  b    <loop>

end:
pop  {{r4-r7, pc}}
long_branch:
  add  r7, #1
  bx   r7
";
         parser.Compile(token, model, start, code.SplitLines());
         PokemonModel.UniquifyName(model, start, ref name);
         model.ObserveAnchorWritten(token, name, new NoInfoRun(start + 1)); // +1: pointers to thumb code carry the thumb bit
         return start;
      }

      private int InsertInit(string name, int tableAddress, int callbackAddress, int originalInit, bool isSecondary, TilesetAnimationConstants constants) {
         var token = tokenFactory();
         var start = model.FindFreeSpace(model.FreeSpaceStart, 0xA0);
         if (start < 0) { start = model.Count; model.ExpandData(token, model.Count + 0x100); }
         var counter = isSecondary ? constants.SecondaryCounter : constants.PrimaryCounter;
         var max = isSecondary ? constants.SecondaryMax : constants.PrimaryMax;
         var callbackVariable = isSecondary ? constants.SecondaryCallback : constants.PrimaryCallback;
         var chain = originalInit == Pointer.NULL ? 0 : (originalInit & ~1) + BaseModel.PointerOffset;
         var code = $@"
push {{r4-r7, lr}}

@ run the tileset's own init first (it sets its counter max and callback)
ldr  r7, =0x{chain:X8}
cmp  r7, #0
beq  <counter>
bl   <long_branch>

counter:
ldr  r0, =0x{counter:X8}
mov  r1, #0
strh r1, [r0, #0]

@ r1 = 1 << (largest entry.timer)
ldr  r2, =<{tableAddress:X6}>
sub  r2, #12
ldr  r3, =0xFEFEFEFE
mov  r1, #0
loop1:
  add  r2, #12
  ldr  r0, [r2, #0]
  cmp  r0, r3
  beq  <done1>
  ldrb r0, [r2, #6]
  cmp  r0, r1
  blo  <loop1>
  mov  r1, r0
  b    <loop1>
done1:
mov  r0, #1
lsl  r0, r1
mov  r1, r0

@ r1 *= every entry.frames
ldr  r2, =<{tableAddress:X6}>
sub  r2, #12
loop2:
  add  r2, #12
  ldr  r0, [r2, #0]
  cmp  r0, r3
  beq  <done2>
  ldrh r0, [r2, #4]
  cmp  r0, #0
  beq  <loop2>
  mul  r1, r0
  b    <loop2>
done2:

@ combine with the original counter max (if any) so both animations stay in sync: max = original * ours, unless that overflows
ldr  r0, =0x{max:X8}
ldrh r2, [r0, #0]
cmp  r2, #0
beq  <store>
mul  r2, r1
ldr  r3, =0x00010000
cmp  r2, r3
bhs  <store>
mov  r1, r2
store:
strh r1, [r0, #0]

@ install the table callback
ldr  r0, =0x{callbackVariable:X8}
ldr  r1, =<{(callbackAddress + 1):X6}>
str  r1, [r0, #0]

pop  {{r4-r7, pc}}
long_branch:
  add  r7, #1
  bx   r7
";
         parser.Compile(token, model, start, code.SplitLines());
         PokemonModel.UniquifyName(model, start, ref name);
         model.ObserveAnchorWritten(token, name, new NoInfoRun(start + 1)); // +1: pointers to thumb code carry the thumb bit
         return start;
      }
   }
}
