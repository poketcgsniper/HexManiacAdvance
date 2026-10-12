using HavenSoft.HexManiac.Core.Models;

namespace HavenSoft.HexManiac.Core.ViewModels {
   /// <summary>
   /// The 'OW Secondary' main-menu button: the secondary overworld data in one place. It opens the field effects table (grass, ripples, shadows, dust, with their palettes);
   /// the Reflection Palettes editor is one button away from there, and the editor has a button back to the table. A game with only one of the two opens that one.
   /// </summary>
   public static class OverworldSecondary {
      public const string ButtonText = "OW Secondary";

      public static bool IsSupported(IDataModel model) => FieldEffects.IsSupported(model) || ReflectionPaletteTab.IsSupported(model);
   }
}
