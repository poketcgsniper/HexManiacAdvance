using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.ViewModels.Tools;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class SpriteGalleryTests : BaseViewModelTestClass {
      public SpriteGalleryTests() {
         SetFullModel(0xFF);
         for (int i = 0x100; i < 0x110; i++) Model[i] = 0;
         Model.SetList(new ModelDelta(), SpriteGalleryElementViewModel.NameListName, "HERO", "RIVAL", "MOM");
         ViewPort.Edit($"@100 ^{HardcodeTablesModel.OverworldSprites}[data::]{SpriteGalleryElementViewModel.NameListName} ");
      }

      [Fact]
      public void Gallery_LabelsShowNumberThenName() {
         var gallery = new SpriteGalleryElementViewModel(ViewPort, 1);

         Assert.Equal(3, gallery.Elements.Count);
         Assert.Equal("1 (RIVAL)", gallery.Elements[1].Label);
         Assert.Equal("RIVAL", gallery.CurrentName);
         Assert.True(gallery.Elements[1].Selected);
         Assert.False(gallery.Elements[0].Selected);
      }

      [Fact]
      public void Gallery_RenamingUpdatesTheListAndTheDropdownNames() {
         var gallery = new SpriteGalleryElementViewModel(ViewPort, 1);

         gallery.CurrentName = "Best Friend";

         Assert.True(Model.TryGetList(SpriteGalleryElementViewModel.NameListName, out var list));
         Assert.Equal(new List<string> { "HERO", "Best_Friend", "MOM" }, list.ToList());
         Assert.True(list.StoredHashMatches);
         Assert.Equal("Best_Friend", Model.GetOptions(HardcodeTablesModel.OverworldSprites)[1]);
         Assert.Equal("1 (Best_Friend)", gallery.Elements[1].Label);
         Assert.Equal("Best_Friend", gallery.CurrentName);
      }

      [Fact]
      public void Gallery_SelectingAnItemMovesTheTableTool() {
         var gallery = new SpriteGalleryElementViewModel(ViewPort, 0);

         gallery.SelectCommand.Execute(gallery.Elements[2]);

         Assert.Equal(0x100 + 8, ViewPort.ConvertViewPointToAddress(ViewPort.SelectionStart));
      }

      [Fact]
      public void TableTool_OnOverworldSprite_IncludesTheGallery() {
         ViewPort.Goto.Execute($"{HardcodeTablesModel.OverworldSprites}/2");

         var gallery = ViewPort.Tools.TableTool.Children.OfType<SpriteGalleryElementViewModel>().Single();

         Assert.Equal(2, gallery.CurrentIndex);
         Assert.Equal("MOM", gallery.CurrentName);
      }
   }
}
