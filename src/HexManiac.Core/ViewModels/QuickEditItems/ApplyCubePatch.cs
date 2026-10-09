using HavenSoft.HexManiac.Core.Models;
using System;
using System.IO;
using System.Threading.Tasks;

namespace HavenSoft.HexManiac.Core.ViewModels.QuickEditItems {
   /// <summary>
   /// Utilities > Misc > "Apply CUBE to a vanilla Emerald ROM...".
   /// Turns an unmodified Pokemon Emerald (USA, Europe) ROM into CUBE (pokeemerald-expansion, built with all of its features)
   /// by applying the BPS patch that ships with the program, and installs CUBE's HexManiac metadata (.toml) for the result.
   ///
   /// The original ROM is never touched: the result is written next to it as "&lt;name&gt; CUBE.gba" with "&lt;name&gt; CUBE.toml",
   /// and opened in a new tab (HexManiac finds the .toml by name, like for any other ROM).
   /// </summary>
   public class ApplyCubePatch : IQuickEditItem, IStandaloneQuickEdit {
      private readonly EditorViewModel editor;
      private readonly ICubeResources resources;
      private readonly VanillaRomSpec vanilla;
      private bool running, statusShown;

      public string Name => "Apply CUBE to a vanilla Emerald ROM...";

      public string Description => @"Cradi's Ultimate Build of Emerald (CUBE).

Turns a vanilla Pokemon Emerald (USA, Europe) ROM into CUBE: the pokeemerald-expansion based build, with all of its features. It also sets up CUBE's HexManiac metadata (the .toml file) for the new ROM.

Your original ROM is NOT changed. A new 32 MB ROM and a matching .toml file are written next to it (for example 'Emerald CUBE.gba' and 'Emerald CUBE.toml'), and the new ROM opens in a new tab.

If the current tab is not a vanilla Emerald ROM, you will be asked to choose one.";

      public string WikiLink => string.Empty;

      public event EventHandler CanRunChanged;

      public ApplyCubePatch(EditorViewModel editor, ICubeResources resources = null, VanillaRomSpec vanilla = null) {
         this.editor = editor;
         this.resources = resources ?? new ShippedCubeResources();
         this.vanilla = vanilla ?? VanillaRomSpec.EmeraldUsV10;
      }

      public bool CanRun(IViewPort viewPort) => true;

      public void TabChanged() => CanRunChanged?.Invoke(this, EventArgs.Empty);

      public async Task<ErrorInfo> Run(IViewPort viewPort) {
         if (running) return new ErrorInfo("CUBE is already being applied.", isWarningLevel: true);
         running = true;
         statusShown = false;
         ErrorInfo result;
         try {
            result = await RunInternal(viewPort);
         } catch (Exception ex) {
            result = new ErrorInfo("Could not apply CUBE: " + ex.Message);
         } finally {
            running = false;
         }
         // the "Building CUBE..." status must not stay on screen next to an error (a success message replaces it by itself)
         if (statusShown && !(result.HasError && result.IsWarning)) editor.ShowStatus(string.Empty);
         return result;
      }

      private async Task<ErrorInfo> RunInternal(IViewPort viewPort) {
         var fileSystem = editor.FileSystem;
         var dispatcher = editor.Singletons.WorkDispatcher;

         // Let the dialog that started this command close before we put up file dialogs of our own.
         await dispatcher.WaitForRenderingAsync();

         // 1. The patch and the .toml that ship with the program.
         var patchFile = resources.ReadPatch();
         var tomlLines = resources.ReadToml();
         if (patchFile == null || tomlLines == null) {
            return new ErrorInfo(
               $"The CUBE files are missing: '{ShippedCubeResources.PatchFileName}' and '{ShippedCubeResources.TomlFileName}' were not found in{Environment.NewLine}" +
               $"{resources.Location}{Environment.NewLine}" +
               "They come with the release version of this program (in its 'resources' folder). Development builds do not include them.");
         }
         var patch = BpsPatch.DecompressIfGzip(patchFile);
         var info = BpsPatch.ReadInfo(patch);
         if (info == null) return new ErrorInfo("The CUBE patch that comes with this program is damaged (it is not a BPS patch).");

         // 2. Which ROM? The current tab if it is an unmodified Emerald, otherwise ask.
         byte[] source = null;
         string sourcePath = null;
         if (viewPort is ViewPort tab && !string.IsNullOrEmpty(tab.FullFileName)) {
            var tabData = tab.Model.RawData;
            if (EmeraldRomCheck.Check(tabData, vanilla, info).IsVanilla) {
               source = (byte[])tabData.Clone(); // the tab stays editable while we work in the background
               sourcePath = tab.FullFileName;
            }
         }
         if (source == null) {
            var file = fileSystem.OpenFile("Vanilla Pokemon Emerald (USA, Europe)", "gba");
            if (file == null) return ErrorInfo.NoError; // the user cancelled the dialog
            var check = EmeraldRomCheck.Check(file.Contents, vanilla, info);
            if (!check.IsVanilla) return new ErrorInfo(check.Message);
            source = file.Contents;
            sourcePath = file.Name;
         }

         // 3. Never overwrite anything: find names for the new files that are not in use.
         if (!TryChooseOutputPaths(fileSystem, sourcePath, out var romPath, out var tomlPath)) {
            return new ErrorInfo($"Could not find an unused name for the CUBE ROM next to {sourcePath}.");
         }

         // 4. Apply the patch (about 32 MB of output) without freezing the window.
         statusShown = true;
         editor.ShowStatus($"Building CUBE from {Path.GetFileName(sourcePath)} ({FormatMegabytes(info.TargetSize)}), this takes a few seconds...");
         BpsApplyResult result = null;
         await dispatcher.RunBackgroundWork(() => result = BpsPatch.Apply(patch, source));
         if (!result.Success) return new ErrorInfo("Could not build CUBE: " + result.Message);

         // 5. Write the new ROM and its .toml.
         var rom = new LoadedFile(romPath, result.Target);
         if (!fileSystem.Save(rom)) {
            // The folder may be read-only: let the user pick another place.
            var chosen = fileSystem.RequestNewName(Path.GetFileName(romPath), "GameBoy Advanced", "gba");
            if (string.IsNullOrEmpty(chosen)) {
               return new ErrorInfo($"CUBE was built, but {romPath} could not be written (is the folder read-only?). Nothing was saved.");
            }
            if (!chosen.EndsWith(".gba", StringComparison.OrdinalIgnoreCase)) chosen += ".gba";
            if (IsSamePath(chosen, sourcePath)) {
               return new ErrorInfo("The original ROM is never overwritten. Run the command again and choose a different file name.");
            }
            romPath = chosen;
            tomlPath = Path.ChangeExtension(romPath, ".toml");
            rom = new LoadedFile(romPath, result.Target);
            if (!fileSystem.Save(rom)) return new ErrorInfo($"CUBE was built, but {romPath} could not be written. Nothing was saved.");
         }
         if (!fileSystem.SaveMetadata(romPath, tomlLines)) {
            return new ErrorInfo($"The CUBE ROM was saved as {romPath}, but its HexManiac metadata ({Path.GetFileName(tomlPath)}) could not be written. Check that the folder is writable, then run this command again.");
         }

         // 6. Open it. HexManiac looks for the .toml next to the ROM, so the CUBE tables and names load with it.
         editor.Open.Execute(rom);

         return new ErrorInfo(
            $"Created {Path.GetFileName(romPath)} ({FormatMegabytes(rom.Contents.Length)}) and {Path.GetFileName(tomlPath)} in {Path.GetDirectoryName(romPath)}. " +
            $"The original {Path.GetFileName(sourcePath)} was not changed.", isWarningLevel: true);
      }

      /// <summary>
      /// "folder/Emerald.gba" becomes "folder/Emerald CUBE.gba" + "folder/Emerald CUBE.toml",
      /// or "Emerald CUBE (2).gba" and so on if either of those files exists already.
      /// </summary>
      private static bool TryChooseOutputPaths(IFileSystem fileSystem, string sourcePath, out string romPath, out string tomlPath) {
         var directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
         var name = Path.GetFileNameWithoutExtension(sourcePath);
         for (int i = 1; i <= 99; i++) {
            var suffix = i == 1 ? " CUBE" : $" CUBE ({i})";
            romPath = Path.Combine(directory, name + suffix + ".gba");
            tomlPath = Path.ChangeExtension(romPath, ".toml");
            if (IsSamePath(romPath, sourcePath)) continue;
            if (fileSystem.Exists(romPath) || fileSystem.Exists(tomlPath)) continue;
            return true;
         }
         romPath = tomlPath = null;
         return false;
      }

      private static bool IsSamePath(string a, string b) {
         try {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
         } catch (Exception) {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
         }
      }

      private static string FormatMegabytes(long length) => length >= (1 << 20) ? $"{length >> 20} MB" : $"{length:N0} bytes";
   }
}
