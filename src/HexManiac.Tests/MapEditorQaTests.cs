using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Code;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using HavenSoft.HexManiac.Core.ViewModels.Images;
using HavenSoft.HexManiac.Core.ViewModels.Map;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Pointer = HavenSoft.HexManiac.Core.ViewModels.DataFormats.Pointer;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   /// <summary>Regression tests for problems found by driving the map editor through all of its commands (QA-MAP).</summary>
   public class MapEditorQaTests : BaseViewModelTestClass {
      private const int SignpostStart = 0x40;

      private ModelArrayElement CreateSignpostElement() {
         ViewPort.Edit($"@{SignpostStart:X2} ^signposts[x:|z y:|z elevation. kind. unused: arg::|s=kind(0=<>|1=<>|2=<>|3=<>|4=<>)]1 ");
         var table = new ModelTable(Model, (ITableRun)Model.GetNextRun(SignpostStart), () => Token);
         return table[0];
      }

      [Fact]
      public void NewSignpost_WithoutAScript_DoesNotFormatAScriptBeforeTheStartOfTheFile() {
         var element = CreateSignpostElement();

         var signpost = new SignpostEventViewModel(element, address => { }) { X = 0, Y = 0, Elevation = 0, Kind = 0, Pointer = Pointer.NULL };

         // a signpost with no script has a null pointer: there is nothing there to format as a script
         Assert.DoesNotContain(Model.All<IFormattedRun>(), run => run.Start < 0 || run.Start >= Model.Count);
         Assert.False(signpost.CanGotoScript);
      }

      #region trainer template

      // name, class, music+gender, sprite, team size, team pointer: 20 bytes per trainer
      private const string TrainerFormat = "[name\"\"12 class. introMusicAndGender. sprite. pokemonCount. pokemon<>]2 ";
      private const int OldTeam = 0x100, NewTeam = 0x140;

      /// <summary>every pointer source that a run claims has to be a pointer that really points at the run</summary>
      private IEnumerable<string> StalePointerSources() {
         foreach (var run in Model.All<IFormattedRun>()) {
            if (run.PointerSources == null) continue;
            foreach (var source in run.PointerSources) {
               if (Model.ReadPointer(source) != run.Start) yield return $"{run.Start:X}<-{source:X}";
            }
         }
      }

      [Fact]
      public void CreateExpansionTrainer_ReusingATrainerSlot_LeavesNoStalePointerSourceToTheOldTeam() {
         // the same trainer exists once per difficulty; each of them points at a team
         ViewPort.Edit($"@40 ^data.trainers.stats{TrainerFormat}");
         ViewPort.Edit($"@80 ^data.trainers.stats.easy{TrainerFormat}");
         ViewPort.Edit($"@C0 ^data.trainers.stats.hard{TrainerFormat}");
         foreach (var pointer in new[] { 0x64, 0xA4, 0xE4 }) ViewPort.Edit($"@{pointer:X2} <{OldTeam:X6}> ");
         Assert.True(!StalePointerSources().Any(), "setup: " + string.Join(" ", StalePointerSources()) + " ptr64=" + Model.ReadPointer(0x64).ToString("X"));
         Assert.Equal(3, Model.GetNextRun(OldTeam).PointerSources.Count);

         var template = new EventTemplate(InstantDispatch.Instance, Model, new ScriptParser(0, new List<IScriptLine>(), 0x02), Enumerable.Range(0, 100).Select(i => (IPixelViewModel)new ReadonlyPixelViewModel(16, 16, new short[256])).ToList());
         var write = typeof(EventTemplate).GetMethod("WriteExpansionTrainer", BindingFlags.NonPublic | BindingFlags.Instance);
         var trainer = new ModelTable(Model, Model.GetTable("data.trainers.stats"), () => Token)[1];
         ViewPort.Edit($"@{NewTeam:X2} 00 ");
         write.Invoke(template, new object[] { Token, trainer, 1, new TrainerPreference(1, 2, 3), 1, NewTeam });

         Assert.Empty(StalePointerSources());
         Assert.Equal(NewTeam, Model.ReadPointer(0x64));
         Assert.Equal(NewTeam, Model.ReadPointer(0xA4));
         Assert.Equal(NewTeam, Model.ReadPointer(0xE4));
         var newTeam = Model.GetNextRun(NewTeam);
         Assert.Equal(NewTeam, newTeam.Start);
         Assert.Equal(new[] { 0x64, 0xA4, 0xE4 }, newTeam.PointerSources.OrderBy(i => i).ToArray());
      }

      #endregion

      #region object event range

      private ObjectEventViewModel CreateObjectViewModel() {
         ViewPort.Edit("@40 ^objects[id. graphics. kind. x:|z y:|z elevation. moveType. range:|t|x::|y:: trainerType: trainerRangeOrBerryID: script<> flag: padding:]1 ");
         var element = new ModelTable(Model, (ITableRun)Model.GetNextRun(0x40), () => Token)[0];
         Model.SetList(Token, "FacingOptions", new[] { "stand", "walk" }, "0");
         return new ObjectEventViewModel(null, address => { }, element, null, new List<IPixelViewModel>(), null, null);
      }

      [Fact]
      public void ObjectRange_NumberAboveFifteen_DoesNotSpillIntoTheOtherRange() {
         var obj = CreateObjectViewModel();
         obj.RangeX = 3; obj.RangeY = 4;

         obj.RangeX = 16 + 7; // only 4 bits belong to X

         Assert.Equal(7, obj.RangeX);
         Assert.Equal(4, obj.RangeY);
      }

      [Fact]
      public void ObjectRange_YNumberAboveFifteen_DoesNotSpillIntoTheNeighboringFields() {
         var obj = CreateObjectViewModel();
         obj.RangeX = 3; obj.RangeY = 5;
         var before = Model.RawData.Clone() as byte[];

         obj.RangeY = 16 + 2;

         Assert.Equal(3, obj.RangeX);
         Assert.Equal(2, obj.RangeY);
         var changed = Enumerable.Range(0, before.Length).Where(i => before[i] != Model.RawData[i]).ToList();
         Assert.Single(changed); // only the range byte
      }

      [Fact]
      public void ObjectRange_TypedAsText_KeepsEachNumberInItsOwnHalf() {
         var obj = CreateObjectViewModel();

         obj.RangeXY = "(20, 33)";

         Assert.Equal(4, obj.RangeX);
         Assert.Equal(1, obj.RangeY);
      }

      #endregion

      #region wave function collapse

      [Fact]
      public void WaveCollapse_EveryOptionCanBeChosen() {
         var cell = new WaveCell(new List<CollapseProbability> { new(10) { Count = 1 }, new(20) { Count = 1 }, new(30) { Count = 1 } }, block => 0);
         var rnd = new Random(1);
         var chosen = new HashSet<int>();

         for (int i = 0; i < 300; i++) chosen.Add(cell.Collapse(rnd) & 0x3FF);

         Assert.Equal(new[] { 10, 20, 30 }, chosen.OrderBy(i => i).ToArray());
      }

      [Fact]
      public void WaveCollapse_OptionsAreChosenInProportionToTheirCount() {
         var cell = new WaveCell(new List<CollapseProbability> { new(10) { Count = 3 }, new(20) { Count = 1 } }, block => 0);
         var rnd = new Random(2);
         int second = 0;

         for (int i = 0; i < 4000; i++) if ((cell.Collapse(rnd) & 0x3FF) == 20) second++;

         Assert.InRange(second, 800, 1200); // one in four
      }

      [Fact]
      public void WaveCollapse_UsesTheCollisionOfTheChosenBlock() {
         var cell = new WaveCell(new List<CollapseProbability> { new(10) { Count = 1 } }, block => 3);

         Assert.Equal((3 << 10) | 10, cell.Collapse(new Random(3)));
      }

      #endregion

      #region Block editor

      [Fact]
      public void BlockEditor_PaletteSelection_IgnoresNumbersThatAreNoPalette() {
         Model.SetList(Token, "MapAttributeBehaviors", "Normal", "Tall Grass", "Water");
         var tiles = new int[1024][,];
         for (int i = 0; i < tiles.Length; i++) tiles[i] = new int[8, 8];
         var palettes = Enumerable.Range(0, 16).Select(_ => new short[16]).ToArray();
         var blocks = Enumerable.Range(0, 4).Select(_ => new byte[16]).ToArray();
         var attributes = Enumerable.Range(0, 4).Select(_ => new byte[2]).ToArray();
         var editor = new BlockEditor(ViewPort.ChangeHistory, Model, new MapTutorialsViewModel(), palettes, tiles, blocks, attributes);
         editor.BlockIndex = 1;
         editor.ShowTiles = true;
         editor.PaletteSelection = 3;

         editor.PaletteSelection = -1;  // a combo box that lost its items writes -1 back
         editor.PaletteSelection = 16;

         Assert.Equal(3, editor.PaletteSelection);
         Assert.NotNull(editor.TileRender);
      }

      #endregion
   }
}
