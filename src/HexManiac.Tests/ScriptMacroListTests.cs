using HavenSoft.HexManiac.Core.Models;
using System.Linq;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class ScriptMacroListTests : BaseViewModelTestClass {
      [Fact]
      public void ScriptMacroList_LoadedAfterTheCodeToolExists_IsStillUsed() {
         // the model loads its metadata on a background thread, so the script editor can be built first
         var before = ViewPort.Tools.CodeTool.ScriptParser;

         Model.SetList(new ModelDelta(), "scriptmacros", new[] { "B6 mycommand species: level. item: species2: level2. item2:" }, null, null);

         var after = ViewPort.Tools.CodeTool.ScriptParser;
         Assert.NotSame(before, after);
         Assert.True(after.RequireCompleteAddresses == before.RequireCompleteAddresses);
         // B6 normally takes 5 bytes; the ROM's own definition takes 11
         var bytes = new byte[] { 0xB6, 0x79, 0x01, 40, 0, 0, 0, 0, 0, 0, 0, 0x02 };
         for (int i = 0; i < bytes.Length; i++) Model[i] = bytes[i];
         var text = after.Parse(Model, 0, bytes.Length);
         Assert.Contains("mycommand", text);
         Assert.DoesNotContain("nop", text);
      }

      [Fact]
      public void ScriptMacroList_UnchangedLines_KeepTheSameParser() {
         Model.SetList(new ModelDelta(), "scriptmacros", new[] { "B6 mycommand species: level." }, null, null);
         var first = ViewPort.Tools.CodeTool.ScriptParser;
         var second = ViewPort.Tools.CodeTool.ScriptParser;
         Assert.Same(first, second);
      }
   }
}
