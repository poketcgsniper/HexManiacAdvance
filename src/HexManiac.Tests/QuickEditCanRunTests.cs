using HavenSoft.HexManiac.Core.ViewModels.QuickEditItems;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   /// <summary>
   /// The Utilities menu asks every quick edit whether it can run each time the user changes tabs,
   /// so a quick edit that is only for one exact file must be able to say "no" without reading the whole file.
   /// </summary>
   public class QuickEditCanRunTests : BaseViewModelTestClass {
      [Fact]
      public void ApplyCFRUPatch_SomethingThatIsNotAViewPort_CanNotRun() {
         Assert.False(new ApplyCFRUPatch().CanRun(null));
      }

      [Fact]
      public void ApplyCFRUPatch_ASmallFile_CanNotRun() {
         Assert.False(new ApplyCFRUPatch().CanRun(ViewPort));
      }

      [Fact]
      public void ApplyCFRUPatch_FireRedCodeButNotTheVanillaSize_CanNotRun() {
         SetGameCode("BPRE0");
         Assert.False(new ApplyCFRUPatch().CanRun(ViewPort));
      }
   }
}
