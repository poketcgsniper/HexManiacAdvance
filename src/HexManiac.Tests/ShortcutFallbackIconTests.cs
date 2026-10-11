using HavenSoft.HexManiac.Core;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.ViewModels;
using HavenSoft.HexManiac.Core.ViewModels.Images;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   // The built-in sprites of the main-menu buttons (ShortcutFallbackIcons): every button has a coloured picture even when the ROM gives it none (an unedited game has no picture for Sound or OW Sprites).

   #region The pictures themselves

   public class ShortcutFallbackIconArtTests {
      private static (int red, int green, int blue) Rgb(short color) => (((color >> 10) & 31) * 255 / 31, ((color >> 5) & 31) * 255 / 31, (color & 31) * 255 / 31);

      [Fact]
      public void ThereIsAPictureForEveryKindOfButton() {
         var kinds = Enum.GetValues(typeof(ShortcutKind)).Cast<ShortcutKind>().ToList();

         Assert.Equal(kinds.Count, ShortcutFallbackIcons.AllKinds.Count);
         foreach (var kind in kinds) {
            Assert.Contains(kind, ShortcutFallbackIcons.AllKinds);
            Assert.NotNull(ShortcutFallbackIcons.Create(kind));
         }
      }

      [Fact]
      public void EveryPicture_HasTheSizeItSays_AndRealPixels() {
         foreach (var kind in ShortcutFallbackIcons.AllKinds) {
            var picture = ShortcutFallbackIcons.Create(kind);
            var (width, height) = ShortcutFallbackIcons.SizeOf(kind);

            Assert.Equal(width, picture.PixelWidth);
            Assert.Equal(height, picture.PixelHeight);
            Assert.Equal(width * height, picture.PixelData.Length);
            Assert.Equal(ShortcutFallbackIcons.Transparent, picture.Transparent);
            Assert.InRange(width, 16, 24);   // the same sizes as the item icons, overworld sprites and tiles the ROMs give these buttons
            Assert.InRange(height, 16, 24);
            Assert.True(picture.PixelData.Count(pixel => pixel != ShortcutFallbackIcons.Transparent) > width * height / 4, $"{kind} is mostly empty");
         }
      }

      [Fact]
      public void EveryPicture_IsInColour_NotOneFlatGlyph() {
         foreach (var kind in ShortcutFallbackIcons.AllKinds) {
            var colors = ShortcutFallbackIcons.Create(kind).PixelData.Where(pixel => pixel != ShortcutFallbackIcons.Transparent).Distinct().ToList();

            Assert.True(colors.Count >= 5, $"{kind} has only {colors.Count} colours: it should have an outline, shades and a highlight like the pictures of the ROMs");
         }
      }

      [Fact]
      public void EveryPicture_ShowsOnADarkBackground() {
         // the glyphs this replaces were drawn in a grey of 33/255 and almost vanished on the dark theme (about 40/255): a good part of every picture must stand out from it
         foreach (var kind in ShortcutFallbackIcons.AllKinds) {
            var opaque = ShortcutFallbackIcons.Create(kind).PixelData.Where(pixel => pixel != ShortcutFallbackIcons.Transparent).ToList();
            var light = opaque.Count(pixel => { var (r, g, b) = Rgb(pixel); return (r + g + b) / 3 >= 90; });

            Assert.True(light * 10 >= opaque.Count * 3, $"{kind}: only {light} of {opaque.Count} pixels are light");
         }
      }

      [Fact]
      public void EveryPicture_ShowsOnALightBackground_Too() {
         foreach (var kind in ShortcutFallbackIcons.AllKinds) {
            var opaque = ShortcutFallbackIcons.Create(kind).PixelData.Where(pixel => pixel != ShortcutFallbackIcons.Transparent).ToList();
            var dark = opaque.Count(pixel => { var (r, g, b) = Rgb(pixel); return (r + g + b) / 3 <= 90; });

            Assert.True(dark >= 8, $"{kind} has no dark outline ({dark} dark pixels)");
         }
      }

      [Fact]
      public void ThePicturesAreDifferentFromEachOther() {
         var all = ShortcutFallbackIcons.AllKinds.Select(kind => string.Join(",", ShortcutFallbackIcons.Create(kind).PixelData)).ToList();

         Assert.Equal(all.Count, all.Distinct().Count());
      }

      [Fact]
      public void ThePicturesAreMadeOfGbaColours_AndNeverOfTheTransparentKey() {
         foreach (var kind in ShortcutFallbackIcons.AllKinds) {
            foreach (var pixel in ShortcutFallbackIcons.Create(kind).PixelData) {
               Assert.Equal(0, pixel & 0x8000); // 15 bits only
            }
         }
      }

      [Fact]
      public void EachButtonGetsItsOwnViewModelAndItsOwnPixels() {
         var first = ShortcutFallbackIcons.Create(ShortcutKind.Sound);
         var second = ShortcutFallbackIcons.Create(ShortcutKind.Sound);

         Assert.NotSame(first, second);               // a view model that lives on would keep the dialog's images alive
         Assert.NotSame(first.PixelData, second.PixelData);
         Assert.Equal(first.PixelData, second.PixelData);
         var before = second.PixelData[0];
         first.PixelData[0] = (short)(first.PixelData[0] == 5 ? 6 : 5);
         Assert.Equal(before, second.PixelData[0]);
         Assert.Equal(before, ShortcutFallbackIcons.Create(ShortcutKind.Sound).PixelData[0]); // and a change to one never reaches the next one made
      }

      [Fact]
      public void ParseArt_MakesGbaColoursFromEightBitColours() {
         var (width, height, pixels) = ShortcutFallbackIcons.ParseArt("a=f80000 b=00f800 c=0000f8", new[] { "a.b", "c.." });

         Assert.Equal(3, width);
         Assert.Equal(2, height);
         Assert.Equal(new short[] { 31 << 10, ShortcutFallbackIcons.Transparent, 31 << 5, 31, ShortcutFallbackIcons.Transparent, ShortcutFallbackIcons.Transparent }, pixels);
      }

      [Fact]
      public void ParseArt_RefusesBrokenArt() {
         Assert.Throws<FormatException>(() => ShortcutFallbackIcons.ParseArt("a=f80000", new[] { "a.", "a" }));       // a short row
         Assert.Throws<FormatException>(() => ShortcutFallbackIcons.ParseArt("a=f80000", new[] { "ab" }));            // no colour for b
         Assert.Throws<FormatException>(() => ShortcutFallbackIcons.ParseArt("a=f8000", new[] { "a" }));              // a colour that is not 6 digits
         Assert.Throws<FormatException>(() => ShortcutFallbackIcons.ParseArt("a=f80000", new string[0]));             // nothing at all
      }
   }

   #endregion

   #region Which kind of button is it

   public class ShortcutFallbackIconIdentifyTests {
      [Theory]
      [InlineData("data.pokemon.stats/1", "Pokemon", ShortcutKind.Pokemon)]
      [InlineData("data.trainers.stats", "Trainers", ShortcutKind.Trainers)]
      [InlineData("data.pokemon.moves.stats.battle", "Moves", ShortcutKind.Moves)]   // the default metadata of the vanilla games
      [InlineData("data.pokemon.moves.stats", "Moves", ShortcutKind.Moves)]          // CUBE.toml
      [InlineData("data.items.stats", "Items", ShortcutKind.Items)]
      [InlineData("maps.bank0.littleroot town.0-9 ", "Maps", ShortcutKind.Maps)]
      [InlineData("maps.0-9 (BOURG-EN-VOL)", "Maps", ShortcutKind.Maps)]             // the French games
      [InlineData("editor:sound", "Sound", ShortcutKind.Sound)]
      [InlineData("editor:animations", "Anim Tiles", ShortcutKind.AnimatedTiles)]
      [InlineData("editor:customization", "Character\nCustomization", ShortcutKind.CharacterCustomization)]
      [InlineData("graphics.overworld.sprites", "OW Sprites", ShortcutKind.OverworldSprites)]
      [InlineData("EDITOR:Sound", "x", ShortcutKind.Sound)]                          // capital letters never mattered to the goto box
      [InlineData("  data.items.stats ", "x", ShortcutKind.Items)]
      [InlineData("editor:music", "x", ShortcutKind.Sound)]                          // the aliases of ViewPort.OpenEditorTab
      [InlineData("editor:cries", "x", ShortcutKind.Sound)]
      [InlineData("editor:tiles", "x", ShortcutKind.AnimatedTiles)]
      [InlineData("editor:character", "x", ShortcutKind.CharacterCustomization)]
      [InlineData("editor:sprites", "x", ShortcutKind.OverworldSprites)]
      [InlineData("editor:gallery", "x", ShortcutKind.OverworldSprites)]
      [InlineData("graphics.pokemon.sprites.front/1", "x", ShortcutKind.Pokemon)]
      [InlineData("graphics.items.sprites", "x", ShortcutKind.Items)]
      [InlineData("sound.tracks", "x", ShortcutKind.Sound)]
      public void TheDestinationSaysWhichKind(string destination, string text, ShortcutKind expected) {
         Assert.Equal(expected, ShortcutFallbackIcons.Identify(destination, text));
      }

      [Theory]
      [InlineData(null, "Music", ShortcutKind.Sound)]
      [InlineData("", "Music", ShortcutKind.Sound)]
      [InlineData("my.songs", "Songs", ShortcutKind.Sound)]
      [InlineData("my.table", "Pokémon", ShortcutKind.Pokemon)]                 // an accent doesn't matter
      [InlineData("my.table", "Trainer Info", ShortcutKind.Trainers)]
      [InlineData("my.table", "Character\nCustomization", ShortcutKind.CharacterCustomization)]
      [InlineData("my.table", "Pokemon Moves", ShortcutKind.Moves)]
      [InlineData("my.table", "Bag items", ShortcutKind.Items)]
      [InlineData("my.table", "World MAP", ShortcutKind.Maps)]
      public void WithoutAHelpfulDestination_TheTextIsTheSecondChance(string destination, string text, ShortcutKind expected) {
         Assert.Equal(expected, ShortcutFallbackIcons.Identify(destination, text));
      }

      [Theory]
      [InlineData("my.table", "Whatever")]
      [InlineData("", "")]
      [InlineData(null, null)]
      [InlineData("editor:unknown", "Something")]
      public void AButtonNobodyKnows_IsGeneric(string destination, string text) {
         Assert.Equal(ShortcutKind.Generic, ShortcutFallbackIcons.Identify(destination, text));
      }

      [Fact]
      public void TheNamesOfTheRealMenuButtons_AllHaveTheirOwnKind() {
         // every button of the main menu of CUBE (data/GotoShortcut entries plus the editor buttons)
         var buttons = new[] {
            ("data.pokemon.stats/1", "Pokemon"), ("data.trainers.stats", "Trainers"), ("data.pokemon.moves.stats", "Moves"), ("data.items.stats", "Items"), ("maps.bank0.littleroot town.0-9 ", "Maps"),
            ("editor:sound", "Sound"), (HardcodeTablesModel.OverworldSprites, "OW Sprites"), ("editor:animations", "Anim Tiles"), ("editor:customization", "Character\nCustomization"),
         };
         var kinds = buttons.Select(button => ShortcutFallbackIcons.Identify(button.Item1, button.Item2)).ToList();

         Assert.Equal(buttons.Length, kinds.Distinct().Count());
         Assert.DoesNotContain(ShortcutKind.Generic, kinds);
      }
   }

   #endregion

   #region The button uses the picture only when it has none

   public class GotoShortcutFallbackImageTests {
      private static IPixelViewModel Frame() => new ReadonlyPixelViewModel(1, 1, new short[1], -1);

      private static GotoShortcutViewModel Create(IPixelViewModel image) => new GotoShortcutViewModel(null, null, image, "anchor", "text");

      private static GotoShortcutViewModel Create(Func<IPixelViewModel> factory) => new GotoShortcutViewModel(null, null, factory, "anchor", "text");

      [Fact]
      public void AButtonWithoutAPicture_ShowsTheFallback() {
         var fallback = Frame();
         var shortcut = Create((IPixelViewModel)null);

         shortcut.SetFallbackImage(() => fallback);

         Assert.Same(fallback, shortcut.Image);
      }

      [Fact]
      public void AButtonWithAFactoryThatHasNoPicture_ShowsTheFallback() {
         var fallback = Frame();
         var shortcut = Create(() => null);
         shortcut.SetFallbackImage(() => fallback);

         Assert.Same(fallback, shortcut.Image);
      }

      [Fact]
      public void AFactoryThatFails_ShowsTheFallback() {
         var fallback = Frame();
         var shortcut = Create(() => throw new InvalidOperationException("no item table"));
         shortcut.SetFallbackImage(() => fallback);

         Assert.Same(fallback, shortcut.Image);
      }

      [Fact]
      public void ThePictureOfTheRom_AlwaysWins_AndTheFallbackIsNeverMade() {
         var calls = 0;
         var own = Frame();
         var withPicture = Create(own);
         withPicture.SetFallbackImage(() => { calls++; return Frame(); });
         var withFactory = Create(() => own);
         withFactory.SetFallbackImage(() => { calls++; return Frame(); });

         Assert.Same(own, withPicture.Image);
         Assert.Same(own, withFactory.Image);
         Assert.Equal(0, calls);
      }

      [Fact]
      public void PreferImage_ThatFindsNothing_AndAButtonThatHadNoPicture_ShowsTheFallback() {
         var fallback = Frame();
         var shortcut = Create((IPixelViewModel)null);
         shortcut.PreferImage(() => null);
         shortcut.SetFallbackImage(() => fallback);

         Assert.Same(fallback, shortcut.Image);
      }

      [Fact]
      public void PreferImage_ThatFindsAPicture_BeatsTheFallback() {
         var preferred = Frame();
         var shortcut = Create((IPixelViewModel)null);
         shortcut.SetFallbackImage(() => Frame());
         shortcut.PreferImage(() => preferred);

         Assert.Same(preferred, shortcut.Image);
      }

      [Fact]
      public void TheFallback_IsMadeWhenTheButtonIsFirstShown_AndOnlyOnce() {
         var calls = 0;
         var fallback = Frame();
         var shortcut = Create(() => null);
         shortcut.SetFallbackImage(() => { calls++; return fallback; });

         Assert.Equal(0, calls); // preparing the dialog draws nothing

         Assert.Same(fallback, shortcut.Image);
         Assert.Same(fallback, shortcut.Image);
         Assert.Equal(1, calls);
      }

      [Fact]
      public void AFallbackThatHasNothing_OrFails_IsAskedOnce_AndIsNotAnException() {
         var calls = 0;
         var shortcut = Create(() => null);
         shortcut.SetFallbackImage(() => { calls++; throw new InvalidOperationException("broken art"); });

         Assert.Null(shortcut.Image);
         Assert.Null(shortcut.Image);
         Assert.Equal(1, calls);
      }

      [Fact]
      public void AnimationWithoutFrames_LeavesTheFallbackToShow() {
         var fallback = Frame();
         var shortcut = Create(() => null);
         shortcut.SetAnimationSource(() => null); // the flower can't be found in this ROM
         shortcut.SetFallbackImage(() => fallback);

         Assert.Same(fallback, shortcut.Image);
         Assert.False(shortcut.IsAnimated);
      }

      [Fact]
      public void AnAnimation_BeatsTheFallback() {
         var frames = new[] { Frame(), Frame() };
         var shortcut = Create(() => null);
         shortcut.SetAnimationSource(() => ShortcutAnimation.Create(frames, new[] { 100, 100 }));
         shortcut.SetFallbackImage(() => Frame());

         Assert.Same(frames[0], shortcut.Image);
         Assert.True(shortcut.IsAnimated);
      }

      [Fact]
      public void Attach_GivesTheBuiltInPictureOfTheKindOfButton() {
         var shortcut = new GotoShortcutViewModel(null, null, (IPixelViewModel)null, "editor:sound", "Sound");

         ShortcutFallbackIcons.Attach(shortcut, "editor:sound");

         Assert.NotNull(shortcut.Image);
         Assert.Equal(ShortcutFallbackIcons.Create(ShortcutKind.Sound).PixelData, shortcut.Image.PixelData);
      }

      [Fact]
      public void Attach_NeverReplacesThePictureOfTheRom() {
         var own = Frame();
         var shortcut = new GotoShortcutViewModel(null, null, own, "data.items.stats", "Items");

         ShortcutFallbackIcons.Attach(shortcut, "data.items.stats");

         Assert.Same(own, shortcut.Image);
         ShortcutFallbackIcons.Attach(null, "data.items.stats"); // a button that isn't there is no problem
      }
   }

   #endregion

   #region The whole main menu of a ROM that has no pictures for some buttons

   public class ShortcutFallbackEditorTests : BaseViewModelTestClass {
      private GotoShortcutViewModel[] ButtonsOfANewEditor() {
         var editor = New.EditorViewModel();
         editor.Add(ViewPort);
         return editor.GotoViewModel.Shortcuts.ToArray();
      }

      [Fact]
      public void SoundAndOverworldSpritesButtons_HaveColouredSprites_WhenTheRomHasNoPictureForThem() {
         // a ROM with the tables of the Sound editor and of the overworld sprites, but no items table with icons and no names for the player's sprites: an unedited game
         CreateTextTable(SoundTab.SongTable, 0x100, "song a", "song b");
         CreateTextTable(HardcodeTablesModel.OverworldSprites, 0x140, "one", "two");

         var buttons = ButtonsOfANewEditor();
         var sound = buttons.Single(button => button.DisplayText == "Sound");
         var sprites = buttons.Single(button => button.DisplayText == "OW Sprites");

         Assert.Equal(ShortcutFallbackIcons.Create(ShortcutKind.Sound).PixelData, sound.Image.PixelData);
         Assert.Equal(ShortcutFallbackIcons.Create(ShortcutKind.OverworldSprites).PixelData, sprites.Image.PixelData);
         Assert.Equal(ShortcutFallbackIcons.SizeOf(ShortcutKind.Sound), (sound.Image.PixelWidth, sound.Image.PixelHeight));
      }

      [Fact]
      public void AShortcutFromTheMetadata_WhoseImageCantBeFound_GetsTheBuiltInPictureOfItsKind() {
         var shortcuts = new[] {
            new StoredGotoShortcut("Items", "data.items.stats/13/icon/", "data.items.stats"),
            new StoredGotoShortcut("Moves", "graphics.items.sprites/298/sprite/", "data.pokemon.moves.stats.battle"),
         };
         Model.LoadMetadata(new StoredMetadata(default, default, default, default, default, default, shortcuts, Singletons.MetadataInfo, default));
         CreateTextTable(HardcodeTablesModel.ItemsTableName, 0x100, "ONE", "TWO");
         CreateTextTable("data.pokemon.moves.stats.battle", 0x120, "AAA", "BBB");

         var buttons = ButtonsOfANewEditor();
         var items = buttons.Single(button => button.DisplayText == "Items");
         var moves = buttons.Single(button => button.DisplayText == "Moves");

         Assert.Equal(ShortcutFallbackIcons.Create(ShortcutKind.Items).PixelData, items.Image.PixelData);
         Assert.Equal(ShortcutFallbackIcons.Create(ShortcutKind.Moves).PixelData, moves.Image.PixelData);
      }

      [Fact]
      public void EveryButtonOfTheMenu_HasAPicture() {
         CreateTextTable(SoundTab.SongTable, 0x100, "song a", "song b");
         CreateTextTable(HardcodeTablesModel.OverworldSprites, 0x140, "one", "two");

         foreach (var button in ButtonsOfANewEditor()) Assert.NotNull(button.Image);
      }
   }

   #endregion
}
