using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Code;
using HavenSoft.HexManiac.Core.ViewModels.Map;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   /// <summary>Templates that write scripts must keep working when a hack changes the script commands or the specials.</summary>
   public class EventTemplateTests : BaseViewModelTestClass {
      private const string VanillaSetWildBattle = "B6 setwildbattle species: level. item:";
      private const string ExpansionSetWildBattle = "B6 setwildbattle species: level. item: species2: level2. item2:";

      private static ScriptParser ParserWith(params string[] engineLines) {
         var lines = engineLines.Select(line => (IScriptLine)new XSEScriptLine(line)).ToList();
         return new ScriptParser(0, lines, 0x02);
      }

      private static List<string> Names(int count, params (int index, string name)[] named) {
         var list = Enumerable.Range(0, count).Select(i => (string)null).ToList();
         foreach (var (index, name) in named) list[index] = name;
         return list;
      }

      #region script command signatures

      [Fact]
      public void ArgumentCount_ComesFromTheScriptReference() {
         Assert.Equal(3, ParserWith(VanillaSetWildBattle).GetArgumentCount("setwildbattle"));
         Assert.Equal(6, ParserWith(ExpansionSetWildBattle).GetArgumentCount("setwildbattle"));
      }

      [Fact]
      public void ArgumentCount_UnknownCommand_IsMinusOne() {
         Assert.Equal(-1, ParserWith(VanillaSetWildBattle).GetArgumentCount("notacommand"));
      }

      [Fact]
      public void BuildCommand_PadsWithZerosUpToTheSignature() {
         Assert.Equal("setwildbattle 1 50 0", ParserWith(VanillaSetWildBattle).BuildCommand("setwildbattle", "1", "50", "0"));
         Assert.Equal("setwildbattle 1 50 0 0 0 0", ParserWith(ExpansionSetWildBattle).BuildCommand("setwildbattle", "1", "50", "0"));
      }

      [Fact]
      public void BuildCommand_PaddedLineCompilesWithTheMatchingEngine() {
         // the line the legendary template used to write has 3 arguments: it doesn't compile against the expansion's 6-argument command
         var parser = ParserWith(ExpansionSetWildBattle);
         var tooShort = "setwildbattle 1 50 0";
         Assert.Throws<ScriptCompileException>(() => parser.CompileWithoutErrors(Token, Model, 0, ref tooShort));

         var line = parser.BuildCommand("setwildbattle", "1", "50", "0");
         var bytes = parser.CompileWithoutErrors(Token, Model, 0, ref line);
         Assert.Equal(1 + 10 + 1, bytes.Length); // the command, its 10 bytes of arguments (2+1+2+2+1+2), and the end of the script
         Assert.Equal(0xB6, bytes[0]);
      }

      [Fact]
      public void CompileWithoutErrors_BadScript_SaysWhatIsWrongInsteadOfReturningNull() {
         var parser = ParserWith(VanillaSetWildBattle);
         var script = "setwildbattle 1";
         var error = Assert.Throws<ScriptCompileException>(() => parser.CompileWithoutErrors(Token, Model, 0, ref script));
         Assert.Contains("expects 3 arguments", error.Message);
         Assert.Single(error.Errors);
      }

      #endregion

      #region legendary

      [Fact]
      public void LegendaryScript_Vanilla_StaysTheSame() {
         SetGameCode("BPEE0");
         var script = EventTemplate.BuildLegendaryScript(Model, ParserWith(VanillaSetWildBattle), 0x0900, -1);
         Assert.Contains("setwildbattle 1 50 0" + Environment.NewLine, script);
         Assert.Contains("setflag 0x08C1", script);
         Assert.Contains("special 0x13B", script);
         Assert.Contains("setflag 0x0900", script);
      }

      [Fact]
      public void LegendaryScript_Expansion_SetWildBattleHasAllItsArguments() {
         SetGameCode("BPEE0");
         var script = EventTemplate.BuildLegendaryScript(Model, ParserWith(ExpansionSetWildBattle), 0x0900, -1);
         Assert.Contains("setwildbattle 1 50 0 0 0 0" + Environment.NewLine, script);
      }

      [Fact]
      public void LegendaryScript_UsesTheSpecialAndFlagNamesOfTheRom() {
         SetGameCode("BPEE0");
         // the expansion renamed the special and moved it: the vanilla number 0x13B is a different special there
         Model.SetList(new ModelDelta(), "specials", Names(0x140, (0x138, "BattleSetup_StartLegendaryBattle"), (0x13B, "DoSealedChamberShakingEffect_Short")), null, null);
         Model.SetList(new ModelDelta(), Flags.FlagListName, Names(0x900, (0x8C3, "FLAG_SYS_CTRL_OBJ_DELETE")), null, null);

         var script = EventTemplate.BuildLegendaryScript(Model, ParserWith(VanillaSetWildBattle), 0x0900, -1);

         Assert.Contains("special BattleSetup_StartLegendaryBattle", script);
         Assert.DoesNotContain("0x13B", script);
         Assert.Contains("setflag 0x08C3", script);
         Assert.Contains("clearflag 0x08C3", script);
      }

      [Fact]
      public void LegendaryScript_VanillaSpecialName_IsAlsoFound() {
         SetGameCode("BPEE0");
         Model.SetList(new ModelDelta(), "specials", Names(0x140, (0x13B, "StartLegendaryBattle")), null, null);
         var script = EventTemplate.BuildLegendaryScript(Model, ParserWith(VanillaSetWildBattle), 0x0900, -1);
         Assert.Contains("special StartLegendaryBattle", script);
      }

      [Fact]
      public void LegendaryScript_FireRed_WaitsForAButtonBeforeTheBattle() {
         SetGameCode("BPRE0");
         var script = EventTemplate.BuildLegendaryScript(Model, ParserWith(VanillaSetWildBattle), 0x0900, 0x123456);
         Assert.Contains("preparemsg <123456>", script);
         Assert.Contains("waitkeypress", script);
         Assert.Contains("setflag 0x0807", script);
         Assert.Contains("special 0x138", script);
      }

      #endregion

      #region trade

      private static string Section(string script, string label) {
         var start = script.IndexOf(label + ":", StringComparison.Ordinal);
         Assert.True(start >= 0, $"no label {label}");
         var end = script.IndexOf("end", start, StringComparison.Ordinal);
         return script.Substring(start, end - start + 3);
      }

      [Fact]
      public void TradeScript_Expansion_RereadsBothSpeciesNamesBeforeComplainingAboutTheWrongPokemon() {
         // the party menu draws "/ 20" (the Pokémon's HP) with the same string buffers the messages use,
         // so after it the wrong-species message needs the names again
         var script = EventTemplate.BuildTradeScript(true, 3, 0x0961, 0x100, 0x110, 0x120, 0x130, 0x140);
         var wrong = Section(script, "wrongspecies");

         var reread = wrong.IndexOf("special2 0x800D GetInGameTradeSpeciesInfo", StringComparison.Ordinal);
         var message = wrong.IndexOf("loadpointer 0 <000140>", StringComparison.Ordinal);
         Assert.True(reread >= 0);
         Assert.True(reread < message);
         Assert.Contains("copyvar 0x8005 0x8008", wrong.Substring(0, reread)); // expansion: the trade number goes in 0x8005
      }

      [Fact]
      public void TradeScript_Vanilla_RereadsBothSpeciesNamesToo() {
         var script = EventTemplate.BuildTradeScript(false, 3, 0x0961, 0x100, 0x110, 0x120, 0x130, 0x140);
         var wrong = Section(script, "wrongspecies");

         var reread = wrong.IndexOf("special2 0x800D GetInGameTradeSpeciesInfo", StringComparison.Ordinal);
         Assert.True(reread >= 0);
         Assert.True(reread < wrong.IndexOf("loadpointer 0 <000140>", StringComparison.Ordinal));
         Assert.Contains("copyvar 0x8004 0x8008", wrong.Substring(0, reread)); // vanilla: the trade number goes in 0x8004
      }

      [Fact]
      public void TradeScript_OtherMessages_DoNotNeedTheNamesAgain() {
         var script = EventTemplate.BuildTradeScript(true, 0, 0x0961, 0x100, 0x110, 0x120, 0x130, 0x140);
         // exactly two reads of the species info: the one at the start and the one before the wrong-species message
         var count = 0;
         for (var i = script.IndexOf("GetInGameTradeSpeciesInfo", StringComparison.Ordinal); i >= 0; i = script.IndexOf("GetInGameTradeSpeciesInfo", i + 1, StringComparison.Ordinal)) count++;
         Assert.Equal(2, count);
      }

      [Fact]
      public void TradeScript_KeepsTheLayoutTheTradeEditorLooksFor() {
         // GetTradeContent finds the messages by the order of the loadpointer commands: info, thanks, success, fail, wrong species
         var script = EventTemplate.BuildTradeScript(true, 0, 0x0961, 0x100, 0x110, 0x120, 0x130, 0x140);
         var order = new[] { "<000100>", "<000110>", "<000120>", "<000130>", "<000140>" }.Select(text => script.IndexOf(text, StringComparison.Ordinal)).ToArray();
         Assert.All(order, position => Assert.True(position >= 0));
         Assert.Equal(order.OrderBy(position => position), order);
      }

      #endregion
   }
}
