using HavenSoft.HexManiac.Core;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.ViewModels;
using HavenSoft.HexManiac.Core.ViewModels.QuickEditItems;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   /// <summary>
   /// Utilities > Misc > "Apply CUBE to a vanilla Emerald ROM...", run against a tiny stand-in for Emerald (0x200 bytes) and for CUBE (0x300 bytes),
   /// with a file system that only exists in memory.
   /// </summary>
   public class ApplyCubePatchTests {
      private const int VanillaLength = 0x200, CubeLength = 0x300, CodeAddress = 0xAC;
      private static readonly string TestDirectory = Path.Combine(Path.GetTempPath(), "cube-apply-test");

      private class TestResources : ICubeResources {
         public byte[] Patch { get; set; }
         public string[] Toml { get; set; }
         public string Location => "test-resources";
         public byte[] ReadPatch() => Patch;
         public string[] ReadToml() => Toml;
      }

      private readonly StubFileSystem fs = new StubFileSystem();
      private readonly HashSet<string> existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      private readonly List<string> savedRoms = new List<string>();
      private readonly Dictionary<string, byte[]> romContents = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
      private readonly Dictionary<string, string[]> savedToml = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

      private readonly byte[] vanilla = new byte[VanillaLength];
      private readonly byte[] cube = new byte[CubeLength];
      private readonly VanillaRomSpec spec;
      private readonly TestResources resources;
      private readonly EditorViewModel editor;
      private readonly ApplyCubePatch item;

      private bool folderIsReadOnly, tomlCannotBeSaved;

      public static readonly string[] CubeToml = {
         "[General]",
         "ApplicationVersion = '''0.6.1'''",
         "",
         "[[NamedAnchors]]",
         "Name = '''cube.test.anchor'''",
         "Address = 0x000100",
         "Format = '''[a: b:]2'''",
      };

      private static string VanillaPath => Path.Combine(TestDirectory, "Emerald.gba");
      private static string CubePath => Path.Combine(TestDirectory, "Emerald CUBE.gba");
      private static string CubeTomlPath => Path.Combine(TestDirectory, "Emerald CUBE.toml");

      public ApplyCubePatchTests() {
         for (int i = 0; i < vanilla.Length; i++) vanilla[i] = (byte)(i * 7 + i / 13);
         Encoding.ASCII.GetBytes("BPEE").CopyTo(vanilla, CodeAddress);
         vanilla[0xBC] = 0;

         // CUBE stand-in: the same header, then new data, a block copied from the vanilla image, and a block repeated from itself.
         Array.Copy(vanilla, cube, 0x100);
         var anchorData = new byte[] { 0x34, 0x12, 0x78, 0x56, 0xBC, 0x9A, 0xF0, 0xDE };
         anchorData.CopyTo(cube, 0x100);
         Array.Copy(vanilla, 0x100, cube, 0x108, 0xF8);
         Array.Copy(cube, 0, cube, 0x200, 0x100);
         var patch = new BpsPatchTests.BpsBuilder(vanilla, cube)
            .SourceRead(0x100)
            .TargetRead(anchorData)
            .SourceCopy(0xF8, 0x100)
            .TargetCopy(0x100, 0)
            .Build();
         var gz = new MemoryStream();
         using (var zip = new GZipStream(gz, CompressionMode.Compress, leaveOpen: true)) zip.Write(patch, 0, patch.Length);

         resources = new TestResources { Patch = gz.ToArray(), Toml = CubeToml };
         spec = new VanillaRomSpec { Name = "Test Emerald", Length = VanillaLength, GameCode = "BPEE", Sha1 = EmeraldRomCheck.ComputeSha1(vanilla) };

         fs.Exists = name => existing.Contains(name);
         fs.Save = file => {
            if (folderIsReadOnly) return false;
            savedRoms.Add(file.Name);
            romContents[file.Name] = file.Contents;
            existing.Add(file.Name);
            return true;
         };
         fs.SaveMetadata = (name, lines) => {
            if (tomlCannotBeSaved) return false;
            savedToml[Path.ChangeExtension(name, ".toml")] = lines;
            existing.Add(Path.ChangeExtension(name, ".toml"));
            return true;
         };
         fs.MetadataFor = name => savedToml.TryGetValue(Path.ChangeExtension(name, ".toml"), out var lines) ? lines : null;
         fs.LoadFile = name => romContents.TryGetValue(name, out var data) ? new LoadedFile(name, data) : null;

         editor = new EditorViewModel(fs, InstantDispatch.Instance);
         item = new ApplyCubePatch(editor, resources, spec);
      }

      private ViewPort AddTab(string path, byte[] data) {
         var model = new PokemonModel((byte[])data.Clone(), singletons: BaseViewModelTestClass.Singletons);
         var viewPort = new ViewPort(path, model, InstantDispatch.Instance, BaseViewModelTestClass.Singletons);
         editor.Add(viewPort);
         return viewPort;
      }

      private static byte[] WithByte(byte[] data, int index, byte value) {
         var copy = (byte[])data.Clone();
         copy[index] = value;
         return copy;
      }

      // ---- registration -------------------------------------------------------------------------------------------

      [Fact]
      public void UtilitiesMisc_ListsTheCubeCommand_ThatWorksWithoutAnOpenTab() {
         var command = editor.QuickEditsMisc.OfType<ApplyCubePatch>().Single();

         Assert.Equal("Apply CUBE to a vanilla Emerald ROM...", command.Name);
         Assert.IsAssignableFrom<IStandaloneQuickEdit>(command);
         Assert.True(command.CanRun(null));
         Assert.False(string.IsNullOrWhiteSpace(command.Description));
      }

      [Fact]
      public void RunQuickEdit_WithNoTabOpen_DoesNotCrash() {
         fs.OpenFile = (description, extensions) => null; // the user cancels the "choose a ROM" dialog
         var command = editor.QuickEditsMisc.OfType<ApplyCubePatch>().Single();

         editor.RunQuickEdit(command);

         Assert.Equal(0, editor.Count);
      }

      // ---- the normal flow ------------------------------------------------------------------------------------------------

      [Fact]
      public void VanillaTabIsOpen_Run_WritesNewRomAndTomlNextToIt() {
         var tab = AddTab(VanillaPath, vanilla);

         var result = item.Run(tab).Result;

         Assert.True(result.IsWarning, result.ErrorMessage);
         Assert.Contains("Emerald CUBE.gba", result.ErrorMessage);
         Assert.Equal(new[] { CubePath }, savedRoms);
         Assert.Equal(cube, romContents[CubePath]);
         Assert.Equal(CubeToml, savedToml[CubeTomlPath]);
      }

      [Fact]
      public void VanillaTabIsOpen_Run_NeverTouchesTheOriginalRomOrItsToml() {
         var tab = AddTab(VanillaPath, vanilla);

         item.Run(tab).Wait();

         Assert.DoesNotContain(VanillaPath, savedRoms);
         Assert.DoesNotContain(Path.ChangeExtension(VanillaPath, ".toml"), savedToml.Keys);
         Assert.Equal(vanilla, tab.Model.RawData);
      }

      [Fact]
      public void VanillaTabIsOpen_Run_OpensTheNewRomInANewTabWithTheCubeMetadata() {
         var tab = AddTab(VanillaPath, vanilla);

         item.Run(tab).Wait();

         Assert.Equal(2, editor.Count);
         var cubeTab = Assert.IsType<ViewPort>(editor.SelectedTab);
         Assert.Equal(Path.GetFullPath(CubePath), cubeTab.FullFileName);
         cubeTab.Model.InitializationWorkload.Wait();
         Assert.Equal(cube, cubeTab.Model.RawData);
         Assert.Equal(0x100, cubeTab.Model.GetAddressFromAnchor(new ModelDelta(), -1, "cube.test.anchor"));
         Assert.Same(tab, editor[0]); // the vanilla tab is still there, unchanged
      }

      [Fact]
      public void NoTabIsOpen_Run_AsksForTheRomAndBuildsCube() {
         string asked = null;
         fs.OpenFile = (description, extensions) => { asked = description; return new LoadedFile(VanillaPath, vanilla); };

         var result = item.Run(null).Result;

         Assert.NotNull(asked);
         Assert.True(result.IsWarning, result.ErrorMessage);
         Assert.Equal(cube, romContents[CubePath]);
         Assert.Equal(1, editor.Count);
      }

      [Fact]
      public void TabIsNotVanilla_Run_AsksForTheRomInstead() {
         var tab = AddTab(Path.Combine(TestDirectory, "Edited.gba"), WithByte(vanilla, 0x150, 0x99));
         var asked = false;
         fs.OpenFile = (description, extensions) => { asked = true; return new LoadedFile(VanillaPath, vanilla); };

         var result = item.Run(tab).Result;

         Assert.True(asked);
         Assert.True(result.IsWarning, result.ErrorMessage);
         Assert.Equal(new[] { CubePath }, savedRoms);
      }

      [Fact]
      public void DialogCancelled_Run_DoesNothing() {
         fs.OpenFile = (description, extensions) => null;

         var result = item.Run(null).Result;

         Assert.False(result.HasError);
         Assert.Empty(savedRoms);
         Assert.Empty(savedToml);
         Assert.Equal(0, editor.Count);
      }

      [Fact]
      public void OutputNameAlreadyExists_Run_PicksTheNextFreeName() {
         existing.Add(CubePath); // an earlier CUBE (maybe with the user's edits)
         var tab = AddTab(VanillaPath, vanilla);

         var result = item.Run(tab).Result;

         var expected = Path.Combine(TestDirectory, "Emerald CUBE (2).gba");
         Assert.True(result.IsWarning, result.ErrorMessage);
         Assert.Equal(new[] { expected }, savedRoms);
         Assert.True(savedToml.ContainsKey(Path.Combine(TestDirectory, "Emerald CUBE (2).toml")));
      }

      [Fact]
      public void OnlyTheTomlAlreadyExists_Run_StillPicksTheNextFreeName() {
         existing.Add(CubeTomlPath); // a stray .toml must not be overwritten either
         var tab = AddTab(VanillaPath, vanilla);

         item.Run(tab).Wait();

         Assert.Equal(new[] { Path.Combine(TestDirectory, "Emerald CUBE (2).gba") }, savedRoms);
         Assert.DoesNotContain(CubeTomlPath, savedToml.Keys);
      }

      // ---- inputs that are not a vanilla Emerald --------------------------------------------------------------------------

      private ErrorInfo RunWithPickedFile(byte[] contents) {
         fs.OpenFile = (description, extensions) => new LoadedFile(VanillaPath, contents);
         return item.Run(null).Result;
      }

      [Fact]
      public void OtherGame_Run_SaysWhichGameItIs() {
         var firered = (byte[])vanilla.Clone();
         Encoding.ASCII.GetBytes("BPRE").CopyTo(firered, CodeAddress);

         var result = RunWithPickedFile(firered);

         Assert.False(result.IsWarning);
         Assert.Contains("FireRed", result.ErrorMessage);
         Assert.Empty(savedRoms);
         Assert.Equal(0, editor.Count);
      }

      [Fact]
      public void OtherLanguageOfEmerald_Run_SaysSo() {
         var german = (byte[])vanilla.Clone();
         Encoding.ASCII.GetBytes("BPED").CopyTo(german, CodeAddress);

         var result = RunWithPickedFile(german);

         Assert.Contains("BPED", result.ErrorMessage);
         Assert.Contains("language", result.ErrorMessage);
         Assert.Empty(savedRoms);
      }

      [Fact]
      public void ModifiedEmerald_Run_SaysItIsNotTheOriginal() {
         var result = RunWithPickedFile(WithByte(vanilla, 0x1F0, 0x99));

         Assert.False(result.IsWarning);
         Assert.Contains("modified", result.ErrorMessage);
         Assert.Empty(savedRoms);
      }

      [Fact]
      public void ExpandedEmerald_Run_SaysItIsTheWrongSize() {
         var expanded = new byte[VanillaLength + 0x300];
         Array.Copy(vanilla, expanded, vanilla.Length);

         var result = RunWithPickedFile(expanded);

         Assert.Contains("expanded", result.ErrorMessage);
         Assert.Empty(savedRoms);
      }

      [Fact]
      public void RomWithCopierHeader_Run_ExplainsTheHeader() {
         var headered = new byte[VanillaLength + 0x200];
         Array.Copy(vanilla, 0, headered, 0x200, vanilla.Length);
         Encoding.ASCII.GetBytes("BPEE").CopyTo(headered, CodeAddress); // the game code is where a headerless ROM has it, the length is off

         var result = RunWithPickedFile(headered);

         Assert.Contains("header", result.ErrorMessage);
         Assert.Empty(savedRoms);
      }

      [Fact]
      public void FileThatIsNotARom_Run_SaysSo() {
         var result = RunWithPickedFile(new byte[0x40]);

         Assert.Contains("too small", result.ErrorMessage);
         Assert.Empty(savedRoms);
      }

      [Fact]
      public void AlreadyCube_Run_SaysThereIsNothingToApply() {
         var cubeCopy = (byte[])cube.Clone();
         Assert.Equal("BPEE", EmeraldRomCheck.ReadGameCode(cubeCopy)); // the stand-in CUBE has Emerald's game code, like the real one

         var result = RunWithPickedFile(cubeCopy);

         Assert.False(result.IsWarning);
         Assert.Contains("already is CUBE", result.ErrorMessage);
         Assert.Empty(savedRoms);
      }

      [Fact]
      public void ChangedCube_Run_SaysItIsProbablyCube() {
         var changedCube = WithByte(cube, 0x250, 0x77);

         var result = RunWithPickedFile(changedCube);

         Assert.Contains("probably already CUBE", result.ErrorMessage);
         Assert.Empty(savedRoms);
      }

      // ---- the patch files and the disk ---------------------------------------------------------------------------------------

      [Fact]
      public void PatchFilesMissing_Run_ExplainsWhatIsMissingAndWhere() {
         resources.Patch = null;
         var tab = AddTab(VanillaPath, vanilla);

         var result = item.Run(tab).Result;

         Assert.False(result.IsWarning);
         Assert.Contains(ShippedCubeResources.PatchFileName, result.ErrorMessage);
         Assert.Contains("test-resources", result.ErrorMessage);
         Assert.Empty(savedRoms);
         Assert.Equal(1, editor.Count);
      }

      [Fact]
      public void TomlMissing_Run_ExplainsWhatIsMissing() {
         resources.Toml = null;
         var tab = AddTab(VanillaPath, vanilla);

         var result = item.Run(tab).Result;

         Assert.Contains(ShippedCubeResources.TomlFileName, result.ErrorMessage);
         Assert.Empty(savedRoms);
      }

      [Fact]
      public void DamagedPatchFile_Run_ReportsItAndWritesNothing() {
         var damaged = BpsPatch.DecompressIfGzip(resources.Patch);
         damaged[damaged.Length - 20] ^= 0x10;
         resources.Patch = damaged;
         var tab = AddTab(VanillaPath, vanilla);

         var result = item.Run(tab).Result;

         Assert.False(result.IsWarning);
         Assert.Contains("damaged", result.ErrorMessage);
         Assert.Empty(savedRoms);
         Assert.Equal(1, editor.Count);
      }

      [Fact]
      public void PatchForAnotherRom_Run_ReportsItAndWritesNothing() {
         // a patch whose source is not what the "vanilla" spec describes: same length, other content, so the ROM check passes but the BPS source checksum does not
         var otherSource = (byte[])vanilla.Clone();
         otherSource[0x1F0] ^= 0xFF;
         resources.Patch = new BpsPatchTests.BpsBuilder(otherSource, cube).SourceRead(0x100).TargetRead(new byte[CubeLength - 0x100]).Build();
         var tab = AddTab(VanillaPath, vanilla);

         var result = item.Run(tab).Result;

         Assert.False(result.IsWarning);
         Assert.Contains("Could not build CUBE", result.ErrorMessage);
         Assert.Empty(savedRoms);
      }

      [Fact]
      public void FolderIsReadOnly_Run_OffersAnotherPlace() {
         folderIsReadOnly = true;
         var elsewhere = Path.Combine(TestDirectory, "elsewhere", "My CUBE.gba");
         string offered = null;
         fs.RequestNewName = (name, description, extensions) => {
            offered = name;
            folderIsReadOnly = false; // the other folder can be written
            return elsewhere;
         };
         var tab = AddTab(VanillaPath, vanilla);

         var result = item.Run(tab).Result;

         Assert.Equal("Emerald CUBE.gba", offered);
         Assert.True(result.IsWarning, result.ErrorMessage);
         Assert.Equal(cube, romContents[elsewhere]);
         Assert.True(savedToml.ContainsKey(Path.ChangeExtension(elsewhere, ".toml")));
         Assert.DoesNotContain(CubeTomlPath, savedToml.Keys);
      }

      [Fact]
      public void FolderIsReadOnly_UserCancelsTheOtherPlace_ReportsAndWritesNothing() {
         folderIsReadOnly = true;
         fs.RequestNewName = (name, description, extensions) => null;
         var tab = AddTab(VanillaPath, vanilla);

         var result = item.Run(tab).Result;

         Assert.False(result.IsWarning);
         Assert.Contains("could not be written", result.ErrorMessage);
         Assert.Empty(savedRoms);
         Assert.Empty(savedToml);
         Assert.Equal(1, editor.Count);
      }

      [Fact]
      public void FolderIsReadOnly_UserPicksTheOriginal_IsRefused() {
         folderIsReadOnly = true;
         fs.RequestNewName = (name, description, extensions) => VanillaPath;
         var tab = AddTab(VanillaPath, vanilla);

         var result = item.Run(tab).Result;

         Assert.False(result.IsWarning);
         Assert.Contains("never overwritten", result.ErrorMessage);
         Assert.Empty(savedRoms);
      }

      [Fact]
      public void TomlCannotBeWritten_Run_ReportsItAndDoesNotOpenTheRomWithoutMetadata() {
         tomlCannotBeSaved = true;
         var tab = AddTab(VanillaPath, vanilla);

         var result = item.Run(tab).Result;

         Assert.False(result.IsWarning);
         Assert.Contains(".toml", result.ErrorMessage);
         Assert.Equal(1, editor.Count);
      }

      [Fact]
      public void RunTwice_SecondRunUsesTheNextName() {
         var tab = AddTab(VanillaPath, vanilla);

         item.Run(tab).Wait();
         item.Run(tab).Wait();

         Assert.Equal(new[] { CubePath, Path.Combine(TestDirectory, "Emerald CUBE (2).gba") }, savedRoms);
      }
   }

   public class EmeraldRomCheckTests {
      private static readonly VanillaRomSpec Spec = new VanillaRomSpec { Name = "Test Emerald", Length = 0x400, GameCode = "BPEE", Sha1 = null };

      private static byte[] Rom(int length, string code) {
         var rom = new byte[length];
         for (int i = 0; i < rom.Length; i++) rom[i] = (byte)(i * 3);
         Encoding.ASCII.GetBytes(code).CopyTo(rom, 0xAC);
         return rom;
      }

      private static VanillaRomSpec SpecFor(byte[] rom) => new VanillaRomSpec { Name = "Test Emerald", Length = rom.Length, GameCode = "BPEE", Sha1 = EmeraldRomCheck.ComputeSha1(rom) };

      [Fact]
      public void Sha1_MatchesTheKnownValueOfAnEmptyAndASimpleInput() {
         Assert.Equal("da39a3ee5e6b4b0d3255bfef95601890afd80709", EmeraldRomCheck.ComputeSha1(new byte[0]));
         Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", EmeraldRomCheck.ComputeSha1(Encoding.ASCII.GetBytes("abc")));
      }

      [Fact]
      public void EmeraldUsV10_SpecMatchesTheRealRom() {
         var realSpec = VanillaRomSpec.EmeraldUsV10;

         Assert.Equal(0x1000000, realSpec.Length);
         Assert.Equal("BPEE", realSpec.GameCode);
         Assert.Equal("f3ae088181bf583e55daf962a92bb46f4f1d07b7", realSpec.Sha1);
      }

      [Fact]
      public void ExactRom_IsVanilla() {
         var rom = Rom(0x400, "BPEE");

         Assert.True(EmeraldRomCheck.Check(rom, SpecFor(rom)).IsVanilla);
      }

      [Fact]
      public void OneChangedByte_IsModified() {
         var rom = Rom(0x400, "BPEE");
         var romSpec = SpecFor(rom);
         rom[0x3FF] ^= 1;

         Assert.Equal(RomCheckKind.Modified, EmeraldRomCheck.Check(rom, romSpec).Kind);
      }

      [Fact]
      public void TooSmall_IsNotARom() {
         Assert.Equal(RomCheckKind.NotARom, EmeraldRomCheck.Check(new byte[10], Spec).Kind);
         Assert.Equal(RomCheckKind.NotARom, EmeraldRomCheck.Check(null, Spec).Kind);
      }

      [Theory]
      [InlineData("BPRE", "FireRed")]
      [InlineData("BPGE", "LeafGreen")]
      [InlineData("AXVE", "Ruby")]
      [InlineData("AXPE", "Sapphire")]
      [InlineData("BPEJ", "language or region")]
      [InlineData("ZZZZ", "ZZZZ")]
      public void OtherGameCodes_AreNamed(string code, string expectedText) {
         var result = EmeraldRomCheck.Check(Rom(0x400, code), Spec);

         Assert.Equal(RomCheckKind.WrongGame, result.Kind);
         Assert.Contains(expectedText, result.Message);
      }

      [Fact]
      public void ExactCubeRom_IsAlreadyPatched() {
         var cubeRom = Rom(0x800, "BPEE");
         var info = new BpsInfo { SourceSize = 0x400, TargetSize = 0x800, TargetCrc32 = (uint)Patcher.CalcCRC32(cubeRom) };

         Assert.Equal(RomCheckKind.AlreadyPatched, EmeraldRomCheck.Check(cubeRom, Spec, info).Kind);
      }

      [Fact]
      public void RomWithCubesLengthButOtherContent_IsWrongSizeAndMentionsCube() {
         var cubeRom = Rom(0x800, "BPEE");
         var info = new BpsInfo { SourceSize = 0x400, TargetSize = 0x800, TargetCrc32 = (uint)Patcher.CalcCRC32(cubeRom) };
         cubeRom[0x700] ^= 1;

         var result = EmeraldRomCheck.Check(cubeRom, Spec, info);

         Assert.Equal(RomCheckKind.WrongSize, result.Kind);
         Assert.Contains("CUBE", result.Message);
      }
   }
}
