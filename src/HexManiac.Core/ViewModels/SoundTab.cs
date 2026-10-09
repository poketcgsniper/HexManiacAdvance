using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.Models.Sound;
using HavenSoft.HexManiac.Core.ViewModels.Images;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;

namespace HavenSoft.HexManiac.Core.ViewModels {
   /// <summary>
   /// The "Sound" tab: a Pokémon cry editor (play / export / import WAV) and a music tool (view, export and insert Sappy/mid2agb .s songs).
   /// </summary>
   public class SoundTab : ViewModelCore, ITabContent {
      public const string CryTable = "sound.pokemon.cry.normal";
      public const string GrowlTable = "sound.pokemon.cry.growl";
      public const string SongTable = "sound.tracks";
      public const string SongNamesList = "songnames";
      public const string SongFileDescription = "Song assembly (Sappy / mid2agb)";
      /// <summary>The extensions the insert-song dialog offers, in order. The first is the dialog's default filter, so .s files are listed right away.</summary>
      public static readonly string[] SongFileExtensions = { "s", "asm", "inc", "txt" };

      private readonly IFileSystem fileSystem;
      private readonly ViewPort viewPort;
      private readonly IDataModel model;
      private readonly StubCommand close = new();

      public static bool IsSupported(IDataModel model) => model.GetTable(CryTable) != null || model.GetTable(SongTable) != null;

      #region ITabContent

      public string Name => "Sound";
      public bool SpartanMode { get; set; }
      public IDataModel Model => model;
      public bool IsMetadataOnlyChange => false;
      public ICommand Save { get; }
      public ICommand SaveAs { get; }
      public ICommand ExportBackup { get; } = new StubCommand();
      public ICommand Undo { get; }
      public ICommand Redo { get; }
      public ICommand Copy { get; } = new StubCommand();
      public ICommand DeepCopy { get; } = new StubCommand();
      public ICommand Diff => null;
      public ICommand DiffLeft => null;
      public ICommand DiffRight => null;
      public ICommand Clear { get; } = new StubCommand();
      public ICommand SelectAll { get; } = new StubCommand();
      public ICommand Goto { get; } = new StubCommand();
      public ICommand ResetAlignment { get; } = new StubCommand();
      public ICommand Back { get; } = new StubCommand();
      public ICommand Forward { get; } = new StubCommand();
      public ICommand Close => close;
      public bool CanDuplicate => false;
      public void Duplicate() { }

      public event EventHandler<string> OnError;
      public event EventHandler<string> OnMessage;
      public event EventHandler Closed;
      public event EventHandler<TabChangeRequestedEventArgs> RequestTabChange;
      event EventHandler ITabContent.ClearMessage { add { } remove { } }
      event EventHandler<Action> ITabContent.RequestDelayedWork { add { } remove { } }
      event EventHandler ITabContent.RequestMenuClose { add { } remove { } }
      event EventHandler<Direction> ITabContent.RequestDiff { add { } remove { } }
      event EventHandler<CanDiffEventArgs> ITabContent.RequestCanDiff { add { } remove { } }
      event EventHandler<CanPatchEventArgs> ITabContent.RequestCanCreatePatch { add { } remove { } }
      event EventHandler<CanPatchEventArgs> ITabContent.RequestCreatePatch { add { } remove { } }
      event EventHandler ITabContent.RequestRefreshGotoShortcuts { add { } remove { } }

      public bool CanIpsPatchRight => false;
      public bool CanUpsPatchRight => false;
      public void IpsPatchRight() { }
      public void UpsPatchRight() { }
      void ITabContent.Refresh() => Reload();
      public bool TryImport(LoadedFile file, IFileSystem fileSystem) {
         if (file == null) return false;
         if (file.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) { ImportCryFromFile(file); return true; }
         if (SongFileExtensions.Any(extension => file.Name.EndsWith("." + extension, StringComparison.OrdinalIgnoreCase))) { InsertSongFromFile(file); return true; }
         return false;
      }

      #endregion

      /// <summary>
      /// Raised when the user wants to hear a cry. The payload is a complete RIFF/WAVE file. The view plays it.
      /// </summary>
      public event EventHandler<byte[]> RequestPlayWav;

      private string status = string.Empty;
      public string Status { get => status; private set => Set(ref status, value); }

      public bool HasCries => model.GetTable(CryTable) != null;
      public bool HasSongs => model.GetTable(SongTable) != null;

      public SoundTab(IFileSystem fileSystem, ViewPort viewPort) {
         this.fileSystem = fileSystem;
         this.viewPort = viewPort;
         model = viewPort.Model;
         Save = viewPort.Save;
         SaveAs = viewPort.SaveAs;
         Undo = ReloadAfter(viewPort.Undo);
         Redo = ReloadAfter(viewPort.Redo);
         close.CanExecute = arg => true;
         close.Execute = arg => Closed?.Invoke(this, EventArgs.Empty);
         Reload();
      }

      /// <summary>
      /// Undo and redo change the ROM behind this tab's back: the lists (songs, voicegroups, slots) have to be read again afterwards.
      /// </summary>
      private ICommand ReloadAfter(ICommand command) {
         var wrapper = new StubCommand {
            CanExecute = arg => command.CanExecute(arg),
            Execute = arg => { command.Execute(arg); Reload(); },
         };
         command.CanExecuteChanged += (sender, e) => wrapper.RaiseCanExecuteChanged();
         return wrapper;
      }

      private StubCommand refresh;
      public ICommand Refresh => StubCommand(ref refresh, Reload, () => true);

      public void Reload() {
         var selectedCry = SelectedCry?.Index ?? -1;
         var selectedSong = SelectedSong?.Index ?? -1;
         LoadCries();
         LoadSongs();
         NotifyPropertyChanged(nameof(HasCries));
         NotifyPropertyChanged(nameof(HasSongs));
         if (selectedCry >= 0 && selectedCry < Cries.Count) SelectedCry = Cries[selectedCry];
         if (selectedSong >= 0 && selectedSong < Songs.Count) SelectedSong = Songs[selectedSong];
         Status = $"{Cries.Count} cries, {Songs.Count} songs, {Voicegroups.Count} voicegroups";
      }

      #region Cries

      public ObservableCollection<CryItem> Cries { get; } = new();

      private string cryFilter = string.Empty;
      public string CryFilter {
         get => cryFilter;
         set {
            if (!TryUpdate(ref cryFilter, value)) return;
            foreach (var item in Cries) item.MatchToFilter(cryFilter);
         }
      }

      private CryItem selectedCry;
      public CryItem SelectedCry {
         get => selectedCry;
         set {
            if (selectedCry == value) return;
            if (selectedCry != null) selectedCry.Selected = false;
            selectedCry = value;
            if (selectedCry != null) selectedCry.Selected = true;
            NotifyPropertyChanged();
            NotifyPropertyChanged(nameof(HasSelectedCry));
            UpdateCryDetails();
            playCry?.RaiseCanExecuteChanged();
            exportCry?.RaiseCanExecuteChanged();
            importCry?.RaiseCanExecuteChanged();
            gotoCry?.RaiseCanExecuteChanged();
         }
      }
      public bool HasSelectedCry => selectedCry?.Sample != null;

      private string cryDetails = string.Empty;
      public string CryDetails { get => cryDetails; private set => Set(ref cryDetails, value); }

      private IPixelViewModel cryWaveform = RenderWaveform(Array.Empty<sbyte>(), 320, 72);
      public IPixelViewModel CryWaveform { get => cryWaveform; private set { cryWaveform = value; NotifyPropertyChanged(); } }

      private bool compressImportedCries = true;
      /// <summary>Store imported cries with DPCM compression (half the size, slightly lossy). This is what the games use for all cries.</summary>
      public bool CompressImportedCries { get => compressImportedCries; set => Set(ref compressImportedCries, value); }

      private int importSampleRateLimit = 22050;
      /// <summary>WAV files with a higher sample rate are resampled down to this rate on import (0 = keep the file's rate).</summary>
      public int ImportSampleRateLimit { get => importSampleRateLimit; set => Set(ref importSampleRateLimit, Math.Max(0, value)); }

      private StubCommand playCry, exportCry, importCry, gotoCry;
      public ICommand PlayCry => StubCommand(ref playCry, ExecutePlayCry, () => HasSelectedCry);
      public ICommand ExportCry => StubCommand(ref exportCry, ExecuteExportCry, () => HasSelectedCry);
      public ICommand ImportCry => StubCommand(ref importCry, ExecuteImportCry, () => selectedCry != null);
      public ICommand GotoCry => StubCommand(ref gotoCry, ExecuteGotoCry, () => selectedCry != null);

      private void LoadCries() {
         Cries.Clear();
         var table = model.GetTable(CryTable);
         if (table == null) return;
         var names = BuildCryNames(table);
         for (int i = 0; i < table.ElementCount; i++) {
            var entry = table.Start + table.ElementLength * i;
            var sampleAddress = model.ReadPointer(entry + 4);
            GbaSample.TryRead(model, sampleAddress, out var sample);
            var name = i < names.Count ? names[i] : string.Empty;
            Cries.Add(new CryItem(i, name, entry, sampleAddress, sample));
         }
         foreach (var item in Cries) item.MatchToFilter(cryFilter);
      }

      /// <summary>
      /// Work out a display name for each cry.
      /// Expansion ROMs name the cry table with a list. Vanilla ROMs map species to cries: species-1 for the first 251, then the hoenn conversion table.
      /// Decomp ROMs with a 'cry' field in the species table map through that field.
      /// </summary>
      private IReadOnlyList<string> BuildCryNames(ITableRun cryTable) {
         var options = model.GetOptions(CryTable);
         if (options != null && options.Count >= cryTable.ElementCount) return options;
         var names = new string[cryTable.ElementCount];
         var species = model.GetTable(HardcodeTablesModel.PokemonNameTable) ?? model.GetTable("data.pokemon.stats");
         var speciesNames = model.GetOptions(HardcodeTablesModel.PokemonNameTable);
         if (speciesNames == null || speciesNames.Count == 0) speciesNames = model.GetOptions("data.pokemon.stats");
         if (speciesNames == null || speciesNames.Count == 0) return names;
         var stats = model.GetTable("data.pokemon.stats");
         var crySegment = stats?.ElementContent.FirstOrDefault(segment => segment.Name == "cry");
         if (stats != null && crySegment != null) {
            int offset = stats.ElementContent.Until(segment => segment == crySegment).Sum(segment => segment.Length);
            int valueOffset = crySegment is ArrayRunEnumSegment enumSegment ? enumSegment.ValueOffset : 1;
            for (int s = 0; s < stats.ElementCount && s < speciesNames.Count; s++) {
               var cry = model.ReadMultiByteValue(stats.Start + stats.ElementLength * s + offset, crySegment.Length) - valueOffset;
               if (cry >= 0 && cry < names.Length && names[cry] == null) names[cry] = speciesNames[s];
            }
         } else {
            for (int s = 1; s <= 251 && s < speciesNames.Count && s - 1 < names.Length; s++) names[s - 1] = speciesNames[s];
            var conversion = model.GetTable("sound.pokemon.cry.hoennconversion");
            if (conversion != null) {
               for (int i = 0; i < conversion.ElementCount; i++) {
                  var cry = model.ReadMultiByteValue(conversion.Start + conversion.ElementLength * i, conversion.ElementContent[0].Length);
                  var s = 277 + i;
                  if (cry >= 0 && cry < names.Length && s < speciesNames.Count && names[cry] == null) names[cry] = speciesNames[s];
               }
            }
         }
         for (int i = 0; i < names.Length; i++) names[i] ??= string.Empty;
         return names;
      }

      private void UpdateCryDetails() {
         if (selectedCry == null) { CryDetails = string.Empty; CryWaveform = RenderWaveform(Array.Empty<sbyte>(), 320, 72); return; }
         var sb = new StringBuilder();
         sb.AppendLine($"Cry {selectedCry.Index}{(string.IsNullOrEmpty(selectedCry.Name) ? "" : ": " + selectedCry.Name)}");
         sb.AppendLine($"Table entry: {CryTable}/{selectedCry.Index} ({selectedCry.EntryAddress:X6})");
         var type = model[selectedCry.EntryAddress];
         sb.AppendLine($"Entry type: {type:X2} ({((type & 0x20) != 0 ? "compressed" : "uncompressed")}{((type & 0x10) != 0 ? ", reversed" : "")}), key {model[selectedCry.EntryAddress + 1]}, length {model[selectedCry.EntryAddress + 2]}, pan {model[selectedCry.EntryAddress + 3]}");
         var growl = model.GetTable(GrowlTable);
         if (growl != null && selectedCry.Index < growl.ElementCount) {
            var growlSample = model.ReadPointer(growl.Start + growl.ElementLength * selectedCry.Index + 4);
            sb.AppendLine(growlSample == selectedCry.SampleAddress ? "Growl (reverse) entry: same sample" : $"Growl (reverse) entry: different sample at {growlSample:X6}");
         }
         if (selectedCry.Sample == null) {
            sb.AppendLine($"Sample at {selectedCry.SampleAddress:X6}: not a valid DirectSound sample.");
            CryDetails = sb.ToString();
            CryWaveform = RenderWaveform(Array.Empty<sbyte>(), 320, 72);
            return;
         }
         var sample = selectedCry.Sample;
         sb.AppendLine($"Sample: {sample.Address:X6}, {(sample.IsCompressed ? "DPCM compressed" : "8-bit PCM")}{(sample.IsLooped ? $", loops from {sample.LoopStart}" : "")}");
         sb.AppendLine($"Sample rate: {sample.SampleRate:0} Hz, {sample.SampleCount} samples ({sample.DurationSeconds:0.00} s), {sample.TotalLength} bytes");
         CryDetails = sb.ToString();
         try {
            CryWaveform = RenderWaveform(sample.Decode(model), 320, 72);
         } catch (Exception) {
            CryWaveform = RenderWaveform(Array.Empty<sbyte>(), 320, 72);
         }
      }

      public static IPixelViewModel RenderWaveform(sbyte[] samples, int width, int height) {
         var pixels = new short[width * height];
         const short background = 0x0C63, midline = 0x294A, wave = 0x7F80; // dark gray, gray, bright cyan-ish (BGR555)
         for (int i = 0; i < pixels.Length; i++) pixels[i] = background;
         for (int x = 0; x < width; x++) pixels[(height / 2) * width + x] = midline;
         if (samples.Length == 0) return new ReadonlyPixelViewModel(width, height, pixels);
         for (int x = 0; x < width; x++) {
            int start = (int)((long)x * samples.Length / width);
            int end = (int)((long)(x + 1) * samples.Length / width);
            if (end <= start) end = start + 1;
            int min = 127, max = -128;
            for (int i = start; i < end && i < samples.Length; i++) { if (samples[i] < min) min = samples[i]; if (samples[i] > max) max = samples[i]; }
            int yTop = (height / 2) - max * (height / 2 - 1) / 128;
            int yBottom = (height / 2) - min * (height / 2 - 1) / 128;
            for (int y = Math.Clamp(yTop, 0, height - 1); y <= Math.Clamp(yBottom, 0, height - 1); y++) pixels[y * width + x] = wave;
         }
         return new ReadonlyPixelViewModel(width, height, pixels);
      }

      private void ExecutePlayCry() {
         if (selectedCry?.Sample == null) return;
         try {
            var pcm = selectedCry.Sample.Decode(model);
            RequestPlayWav?.Invoke(this, GbaSample.ToWav(pcm, (int)Math.Round(selectedCry.Sample.SampleRate)));
         } catch (Exception e) {
            OnError?.Invoke(this, "Could not decode the cry: " + e.Message);
         }
      }

      private void ExecuteExportCry() {
         if (selectedCry?.Sample == null) return;
         var defaultName = string.IsNullOrEmpty(selectedCry.Name) ? $"cry_{selectedCry.Index}" : selectedCry.Name.ToLower().Replace(' ', '_');
         var name = fileSystem.RequestNewName(defaultName + ".wav", "Wave audio", "wav");
         if (string.IsNullOrEmpty(name)) return;
         var pcm = selectedCry.Sample.Decode(model);
         var wav = GbaSample.ToWav(pcm, (int)Math.Round(selectedCry.Sample.SampleRate));
         if (fileSystem.Save(new LoadedFile(name, wav))) Status = $"Exported cry {selectedCry.Index} to {name}";
      }

      private void ExecuteImportCry() {
         if (selectedCry == null) return;
         var file = fileSystem.OpenFile("Wave audio", "wav");
         if (file == null) return;
         ImportCryFromFile(file);
      }

      private void ImportCryFromFile(LoadedFile file) {
         if (selectedCry == null) { OnError?.Invoke(this, "Select a cry to replace first."); return; }
         if (!GbaSample.TryReadWav(file.Contents, out var samples, out var rate, out var error)) { OnError?.Invoke(this, error); return; }
         if (samples.Length == 0) { OnError?.Invoke(this, "The WAV file has no audio."); return; }
         if (ImportSampleRateLimit > 0 && rate > ImportSampleRateLimit) {
            samples = GbaSample.Resample(samples, rate, ImportSampleRateLimit);
            rate = ImportSampleRateLimit;
         }
         var pcm = GbaSample.Quantize(samples);
         var bytes = GbaSample.Build(pcm, rate, CompressImportedCries);
         var token = viewPort.CurrentChange;
         if (!VoicegroupEditor.TryAllocate(model, token, bytes.Length, out var newAddress, out var allocationError)) { OnError?.Invoke(this, allocationError); return; }
         token.ChangeData(model, newAddress, bytes);

         var oldSample = selectedCry.SampleAddress;
         int index = selectedCry.Index;
         RepointCry(CryTable, index, newAddress, CompressImportedCries, token);
         RepointCry(GrowlTable, index, newAddress, CompressImportedCries, token);

         // free the old sample if nothing else uses it
         if (GbaSample.TryRead(model, oldSample, out var old) && !AnyCryUses(oldSample)) {
            model.ClearFormatAndData(token, old.Address, old.TotalLength);
         }
         viewPort.ChangeHistory.ChangeCompleted();
         viewPort.Refresh();
         LoadCries();
         SelectedCry = index < Cries.Count ? Cries[index] : null;
         Status = $"Imported {file.Name} as cry {index}: {pcm.Length} samples at {rate} Hz, {bytes.Length} bytes at {newAddress:X6}.";
         OnMessage?.Invoke(this, Status);
      }

      private void RepointCry(string tableName, int index, int sampleAddress, bool compressed, ModelDelta token) {
         var table = model.GetTable(tableName);
         if (table == null || index >= table.ElementCount) return;
         var element = new ModelArrayElement(model, table.Start, index, () => token, table);
         var type = model[table.Start + table.ElementLength * index];
         type = (byte)((type & 0x10) | (compressed ? 0x20 : 0x00));
         element.SetValue("type", type);
         element.SetAddress("p", sampleAddress);
      }

      private bool AnyCryUses(int sampleAddress) {
         foreach (var tableName in new[] { CryTable, GrowlTable }) {
            var table = model.GetTable(tableName);
            if (table == null) continue;
            for (int i = 0; i < table.ElementCount; i++) {
               if (model.ReadPointer(table.Start + table.ElementLength * i + 4) == sampleAddress) return true;
            }
         }
         return false;
      }

      private void ExecuteGotoCry() {
         if (selectedCry == null) return;
         viewPort.Goto.Execute($"{CryTable}/{selectedCry.Index}");
         RequestTabChange?.Invoke(this, new TabChangeRequestedEventArgs(viewPort));
      }

      #endregion

      #region Songs

      public ObservableCollection<SongItem> Songs { get; } = new();
      public ObservableCollection<VoicegroupItem> Voicegroups { get; } = new();
      public ObservableCollection<string> MusicPlayers { get; } = new() { "0: Music (BGM)", "1: Sound effect 1", "2: Sound effect 2", "3: Sound effect 3" };

      private string songFilter = string.Empty;
      public string SongFilter {
         get => songFilter;
         set {
            if (!TryUpdate(ref songFilter, value)) return;
            foreach (var item in Songs) item.MatchToFilter(songFilter);
         }
      }

      private SongItem selectedSong;
      public SongItem SelectedSong {
         get => selectedSong;
         set {
            if (selectedSong == value) return;
            if (selectedSong != null) selectedSong.Selected = false;
            selectedSong = value;
            if (selectedSong != null) selectedSong.Selected = true;
            NotifyPropertyChanged();
            NotifyPropertyChanged(nameof(HasSelectedSong));
            NotifyPropertyChanged(nameof(SelectedSongName));
            NotifyPropertyChanged(nameof(CanRenameSong));
            UpdateSongDetails();
            exportSong?.RaiseCanExecuteChanged();
            gotoSong?.RaiseCanExecuteChanged();
            playSong?.RaiseCanExecuteChanged();
            exportSongWav?.RaiseCanExecuteChanged();
         }
      }
      public bool HasSelectedSong => selectedSong?.Header != null;

      /// <summary>Songs can be given a name whether or not their header is readable (a custom track inserted by hand has none yet).</summary>
      public bool CanRenameSong => selectedSong != null && model.TryGetList(SongNamesList, out _);

      /// <summary>
      /// The selected song's name, as the song name list (shown in the music dropdowns and the table) has it.
      /// Editing it updates that list in the ROM's metadata (.toml); the ROM itself doesn't store song names.
      /// </summary>
      public string SelectedSongName {
         get => selectedSong?.Name ?? string.Empty;
         set => RenameSelectedSong(value);
      }

      private void RenameSelectedSong(string name) {
         if (selectedSong == null) return;
         if (!model.TryGetList(SongNamesList, out var list)) {
            OnError?.Invoke(this, "This ROM's metadata has no song name list to update.");
            return;
         }
         // list entries can't have spaces: they are used as identifiers in dropdowns and scripts
         var cleaned = (name ?? string.Empty).Trim().Replace(' ', '_');
         var index = selectedSong.Index;
         if (string.IsNullOrEmpty(cleaned)) cleaned = index.ToString();
         var names = list.ToList();
         while (names.Count <= index) names.Add(names.Count.ToString());
         if (names[index] == cleaned) {
            NotifyPropertyChanged(nameof(SelectedSongName)); // show the cleaned-up text again
            return;
         }
         names[index] = cleaned;
         model.SetList(viewPort.CurrentChange, SongNamesList, names, list.Comments, StoredList.GenerateHash(names));
         model.ClearCacheScope();
         viewPort.ChangeHistory.ChangeCompleted();
         selectedSong.Name = cleaned;
         NotifyPropertyChanged(nameof(SelectedSongName));
         UpdateSongDetails();
         Status = $"Song {index} is now called {cleaned}.";
      }

      private string songDetails = string.Empty;
      public string SongDetails { get => songDetails; private set => Set(ref songDetails, value); }

      private string songText = string.Empty;
      /// <summary>The selected song, disassembled into mid2agb .s format.</summary>
      public string SongText { get => songText; private set => Set(ref songText, value); }

      private VoicegroupItem selectedVoicegroup;
      public VoicegroupItem SelectedVoicegroup { get => selectedVoicegroup; set { selectedVoicegroup = value; NotifyPropertyChanged(); } }

      private bool overrideVoicegroup;
      /// <summary>When set, inserted songs always use the selected voicegroup, even if the .s file names one.</summary>
      public bool OverrideVoicegroup { get => overrideVoicegroup; set => Set(ref overrideVoicegroup, value); }

      private bool insertAsNewSong;
      /// <summary>When set, inserted songs are appended to the song table as a new entry instead of replacing the selected song.</summary>
      public bool InsertAsNewSong { get => insertAsNewSong; set => Set(ref insertAsNewSong, value, old => NotifyPropertyChanged(nameof(InsertReplacesSong))); }
      /// <summary>The opposite of InsertAsNewSong, for binding a pair of radio buttons.</summary>
      public bool InsertReplacesSong { get => !insertAsNewSong; set => InsertAsNewSong = !value; }

      private bool insertWithNewVoicegroup;
      /// <summary>
      /// When set, an inserted song gets its own new voicegroup (a copy of the one it would have used) so any instrument of the game can be put in it,
      /// instead of being tied to a voicegroup other songs share.
      /// </summary>
      public bool InsertWithNewVoicegroup { get => insertWithNewVoicegroup; set => Set(ref insertWithNewVoicegroup, value); }

      private int insertMusicPlayer;
      public int InsertMusicPlayer { get => insertMusicPlayer; set => Set(ref insertMusicPlayer, value.LimitToRange(0, 3)); }

      private StubCommand exportSong, insertSong, gotoSong, playSong, exportSongWav, stopSong;
      public ICommand ExportSong => StubCommand(ref exportSong, ExecuteExportSong, () => HasSelectedSong);
      /// <summary>Render the selected song with a software version of the game's music engine and play it.</summary>
      public ICommand PlaySong => StubCommand(ref playSong, ExecutePlaySong, () => HasSelectedSong);
      public ICommand ExportSongWav => StubCommand(ref exportSongWav, ExecuteExportSongWav, () => HasSelectedSong);
      public ICommand StopSong => StubCommand(ref stopSong, () => RequestStopPlayback?.Invoke(this, EventArgs.Empty), () => true);

      public event EventHandler RequestStopPlayback;

      private byte[] RenderSelectedSong() {
         if (selectedSong?.Header == null) return null;
         if (selectedSong.Header.TrackCount == 0) { OnError?.Invoke(this, "This song has no tracks."); return null; }
         try {
            var renderer = new M4aRenderer(model);
            return renderer.RenderWav(selectedSong.Header);
         } catch (Exception e) {
            OnError?.Invoke(this, "Could not render the song: " + e.Message);
            return null;
         }
      }

      private void ExecutePlaySong() {
         var wav = RenderSelectedSong();
         if (wav == null) return;
         Status = $"Playing song {selectedSong.Index}{(string.IsNullOrEmpty(selectedSong.Name) ? "" : " (" + selectedSong.Name + ")")}: {(wav.Length - 44) / 4.0 / M4aRenderer.OutputRate:0.#} seconds (one loop, then a fade). This is a preview: the real game may sound a little different.";
         RequestPlayWav?.Invoke(this, wav);
      }

      private void ExecuteExportSongWav() {
         if (selectedSong?.Header == null) return;
         var defaultName = string.IsNullOrEmpty(selectedSong.Name) ? $"song_{selectedSong.Index}" : selectedSong.Name.ToLower();
         var name = fileSystem.RequestNewName(defaultName + ".wav", "Wave audio", "wav");
         if (string.IsNullOrEmpty(name)) return;
         var wav = RenderSelectedSong();
         if (wav == null) return;
         if (fileSystem.Save(new LoadedFile(name, wav))) Status = $"Exported song {selectedSong.Index} to {name}";
      }
      public ICommand InsertSong => StubCommand(ref insertSong, ExecuteInsertSong, () => HasSongs);
      public ICommand GotoSong => StubCommand(ref gotoSong, ExecuteGotoSong, () => selectedSong != null);

      private void LoadSongs() {
         Songs.Clear();
         Voicegroups.Clear();
         var table = model.GetTable(SongTable);
         if (table == null) { SelectedSong = null; return; }
         IReadOnlyList<string> names = model.GetOptions(SongTable);
         if ((names == null || names.Count == 0) && model.TryGetList(SongNamesList, out var songNames)) names = songNames;
         var usages = new List<(int voicegroup, string song)>();
         for (int i = 0; i < table.ElementCount; i++) {
            var entry = table.Start + table.ElementLength * i;
            var headerAddress = model.ReadPointer(entry);
            SongHeader.TryRead(model, headerAddress, out var header);
            var name = names != null && i < names.Count ? names[i] : string.Empty;
            var musicPlayer = model.ReadMultiByteValue(entry + 4, 2);
            Songs.Add(new SongItem(i, name, entry, headerAddress, header, musicPlayer));
            if (header != null && header.TrackCount > 0) usages.Add((header.Voicegroup, string.IsNullOrEmpty(name) ? i.ToString() : name));
         }
         foreach (var info in VoicegroupCatalog.Scan(model, usages)) Voicegroups.Add(new VoicegroupItem(model, info));
         foreach (var item in Songs) item.MatchToFilter(songFilter);
         if (Voicegroups.Count > 0) SelectedVoicegroup = Voicegroups[0];
         // the selected song belongs to the old list: drop it (callers that want a selection pick one again)
         if (selectedSong != null && !Songs.Contains(selectedSong)) SelectedSong = null;
      }

      private void UpdateSongDetails() {
         if (selectedSong == null) { SongDetails = string.Empty; SongText = string.Empty; SyncVoicegroupSelection(); return; }
         var sb = new StringBuilder();
         sb.AppendLine($"Song {selectedSong.Index}{(string.IsNullOrEmpty(selectedSong.Name) ? "" : ": " + selectedSong.Name)}");
         sb.AppendLine($"Table entry: {SongTable}/{selectedSong.Index} ({selectedSong.EntryAddress:X6}), music player {selectedSong.MusicPlayer}");
         if (selectedSong.Header == null) {
            sb.AppendLine($"Header at {selectedSong.HeaderAddress:X6} is not a valid song header.");
            SongDetails = sb.ToString();
            SongText = string.Empty;
            SyncVoicegroupSelection();
            return;
         }
         var header = selectedSong.Header;
         sb.AppendLine($"Header: {header.Address:X6}, {header.TrackCount} tracks, priority {header.Priority}, reverb {header.Reverb}");
         sb.AppendLine($"Voicegroup: {DescribeVoicegroup(header.Voicegroup)}");
         SongDetails = sb.ToString();
         SyncVoicegroupSelection();
         if (header.TrackCount == 0) { SongText = "(no tracks)"; return; }
         try {
            var name = string.IsNullOrEmpty(selectedSong.Name) ? $"song_{selectedSong.Index}" : selectedSong.Name.ToLower();
            SongText = new SongDisassembler(model).Disassemble(header, name);
            var match = Voicegroups.FirstOrDefault(group => group.Address == header.Voicegroup);
            if (match != null) SelectedVoicegroup = match;
         } catch (Exception e) {
            SongText = "Could not disassemble this song: " + e.Message;
         }
      }

      private void ExecuteExportSong() {
         if (selectedSong?.Header == null) return;
         var defaultName = string.IsNullOrEmpty(selectedSong.Name) ? $"song_{selectedSong.Index}" : selectedSong.Name.ToLower();
         var name = fileSystem.RequestNewName(defaultName + ".s", SongFileDescription, "s");
         if (string.IsNullOrEmpty(name)) return;
         if (fileSystem.Save(new LoadedFile(name, Encoding.UTF8.GetBytes(SongText)))) Status = $"Exported song {selectedSong.Index} to {name}";
      }

      private void ExecuteInsertSong() {
         var file = fileSystem.OpenFile(SongFileDescription, SongFileExtensions);
         if (file == null) return;
         InsertSongFromFile(file);
      }

      private void InsertSongFromFile(LoadedFile file) {
         var table = model.GetTable(SongTable);
         if (table == null) { OnError?.Invoke(this, $"This ROM has no {SongTable} table."); return; }
         if (!InsertAsNewSong && selectedSong == null) { OnError?.Invoke(this, "Select a song to replace, or choose 'add as new song'."); return; }
         var text = Encoding.UTF8.GetString(file.Contents);

         // first pass: find out how big the song is and which symbols it needs
         var externals = new Dictionary<string, int>();
         var result = SongAssembler.Assemble(text, 0, externals);
         if (!result.Success && result.UndefinedSymbols.Count > 0) {
            if (selectedVoicegroup == null) { OnError?.Invoke(this, $"The song needs these symbols, but no voicegroup is selected: {string.Join(", ", result.UndefinedSymbols)}"); return; }
            var unresolved = new List<string>();
            foreach (var symbol in result.UndefinedSymbols) {
               if (symbol.Contains("grp", StringComparison.OrdinalIgnoreCase) || symbol.Contains("voice", StringComparison.OrdinalIgnoreCase) || result.UndefinedSymbols.Count == 1) {
                  externals[symbol] = selectedVoicegroup.Address + BaseModel.PointerOffset;
               } else {
                  unresolved.Add(symbol);
               }
            }
            if (unresolved.Count > 0) { OnError?.Invoke(this, $"The song uses symbols this ROM doesn't know: {string.Join(", ", unresolved)}"); return; }
            result = SongAssembler.Assemble(text, 0, externals);
         }
         if (!result.Success) { OnError?.Invoke(this, "Could not assemble the song: " + result.Error); return; }

         // second pass: assemble for real at the chosen address
         var token = viewPort.CurrentChange;
         if (!VoicegroupEditor.TryAllocate(model, token, result.Bytes.Length + 4, out var address, out var allocationError)) { OnError?.Invoke(this, allocationError); return; }
         while (address % 4 != 0) address++;
         result = SongAssembler.Assemble(text, address + BaseModel.PointerOffset, externals);
         if (!result.Success) { OnError?.Invoke(this, "Could not assemble the song: " + result.Error); return; }
         var bytes = result.Bytes;
         if (OverrideVoicegroup && selectedVoicegroup != null) {
            var pointer = selectedVoicegroup.Address + BaseModel.PointerOffset;
            for (int i = 0; i < 4; i++) bytes[result.HeaderOffset + 4 + i] = (byte)(pointer >> (8 * i));
         }
         token.ChangeData(model, address, bytes);
         var headerAddress = address + result.HeaderOffset;

         // give the song a voicegroup of its own, so every instrument of the game can be used in it
         var voicegroupMessage = string.Empty;
         if (InsertWithNewVoicegroup) {
            SongHeader.TryRead(model, headerAddress, out var insertedHeader);
            var sourceAddress = insertedHeader?.Voicegroup ?? selectedVoicegroup?.Address ?? -1;
            var source = FindVoicegroupItem(sourceAddress)?.Info ?? VoicegroupCatalog.Inspect(model, sourceAddress);
            var voicegroupSongName = !string.IsNullOrEmpty(result.SongName) ? result.SongName : selectedSong?.Name;
            if (VoicegroupEditor.TryCreateCopy(model, token, source, voicegroupSongName, out var voicegroupAddress, out var voicegroupAnchor, out var voicegroupError)) {
               VoicegroupEditor.AssignToSong(model, token, headerAddress, voicegroupAddress);
               voicegroupMessage = $" It has its own new voicegroup at {voicegroupAddress:X6}{(voicegroupAnchor == null ? string.Empty : " (" + voicegroupAnchor + ")")}.";
            } else {
               OnError?.Invoke(this, "The song is inserted, but it could not get its own voicegroup: " + voicegroupError);
            }
         }

         int index;
         if (InsertAsNewSong) {
            var originalStart = table.Start;
            table = model.RelocateForExpansion(token, table, table.Length + table.ElementLength);
            table = table.Append(token, 1);
            model.ObserveRunWritten(token, table);
            index = table.ElementCount - 1;
            if (table.Start != originalStart) OnMessage?.Invoke(this, $"The song table was moved to {table.Start:X6} to make room.");
         } else {
            index = selectedSong.Index;
         }
         var element = new ModelArrayElement(model, table.Start, index, () => token, table);
         element.SetAddress("pointer", headerAddress);
         if (InsertAsNewSong) {
            element.SetValue("musicplayer", InsertMusicPlayer);
            element.SetValue("unknown", InsertMusicPlayer);
         }
         viewPort.ChangeHistory.ChangeCompleted();
         viewPort.Refresh();
         LoadSongs();
         SelectedSong = index < Songs.Count ? Songs[index] : null;
         Status = $"Inserted {file.Name} ({result.SongName}, {bytes.Length} bytes) at {address:X6} as song {index}.{voicegroupMessage}";
         OnMessage?.Invoke(this, Status + (InsertAsNewSong ? $" Play it with 'playbgm {index}' / 'playse {index}'." : string.Empty));
      }

      private void ExecuteGotoSong() {
         if (selectedSong == null) return;
         viewPort.Goto.Execute($"{SongTable}/{selectedSong.Index}");
         RequestTabChange?.Invoke(this, new TabChangeRequestedEventArgs(viewPort));
      }

      #endregion

      #region Voicegroups of the selected song

      private bool updatingVoicegroupSelection;

      private VoicegroupItem songVoicegroup;
      /// <summary>
      /// The voicegroup in the selected song's header. Choosing another one rewrites the header's pointer (undoable);
      /// the next Play uses it.
      /// </summary>
      public VoicegroupItem SongVoicegroup {
         get => songVoicegroup;
         set {
            if (value == songVoicegroup) return;
            if (updatingVoicegroupSelection) { songVoicegroup = value; NotifyPropertyChanged(); return; }
            if (value == null || !HasSelectedSong) return; // the list was reloaded or the selection cleared: nothing to apply
            ChangeSongVoicegroup(value);
         }
      }

      /// <summary>The voicegroup choices are only meaningful for a song with a readable header.</summary>
      public bool CanChangeVoicegroup => HasSelectedSong;

      private string voicegroupNote = string.Empty;
      /// <summary>Says whether other songs share the selected song's voicegroup.</summary>
      public string VoicegroupNote { get => voicegroupNote; private set => Set(ref voicegroupNote, value); }

      private VoicegroupItem newVoicegroupSource;
      /// <summary>The voicegroup a new voicegroup is copied from (it starts out as the selected song's own).</summary>
      public VoicegroupItem NewVoicegroupSource { get => newVoicegroupSource; set => UpdateReference(ref newVoicegroupSource, value); }

      private StubCommand newVoicegroup, gotoVoicegroup, previewSlot, previewInstrument, useInstrument;
      /// <summary>Gives the selected song a new voicegroup of its own: a copy of NewVoicegroupSource with room for all 128 instruments.</summary>
      public ICommand NewVoicegroup => StubCommand(ref newVoicegroup, ExecuteNewVoicegroup, () => HasSelectedSong);
      public ICommand GotoVoicegroup => StubCommand(ref gotoVoicegroup, ExecuteGotoVoicegroup, () => songVoicegroup != null && HasSelectedSong);
      /// <summary>Plays the instrument in the chosen slot of the song's voicegroup.</summary>
      public ICommand PreviewSlot => StubCommand(ref previewSlot, ExecutePreviewSlot, () => selectedSlot != null && HasSelectedSong);
      /// <summary>Plays the instrument chosen in the picker.</summary>
      public ICommand PreviewInstrument => StubCommand(ref previewInstrument, ExecutePreviewInstrument, () => selectedInstrument != null);
      /// <summary>Copies the instrument chosen in the picker into the chosen slot of the song's voicegroup.</summary>
      public ICommand UseInstrument => StubCommand(ref useInstrument, ExecuteUseInstrument, () => selectedInstrument != null && selectedSlot != null && HasSelectedSong);

      /// <summary>TryUpdate for view model objects, which are compared by reference.</summary>
      private bool UpdateReference<T>(ref T field, T value, [CallerMemberName] string propertyName = null) where T : class {
         if (ReferenceEquals(field, value)) return false;
         field = value;
         NotifyPropertyChanged(propertyName);
         return true;
      }

      private void RaiseVoicegroupCommandsChanged() {
         newVoicegroup?.RaiseCanExecuteChanged();
         gotoVoicegroup?.RaiseCanExecuteChanged();
         previewSlot?.RaiseCanExecuteChanged();
         previewInstrument?.RaiseCanExecuteChanged();
         useInstrument?.RaiseCanExecuteChanged();
      }

      private int previewKey = 60;
      /// <summary>The note (0-127, 60 is middle C) played by the instrument previews. Drum kits and key splits answer with a different instrument for every note.</summary>
      public int PreviewKey { get => previewKey; set => Set(ref previewKey, value.LimitToRange(0, 127)); }

      #region Slots of the song's voicegroup

      public ObservableCollection<InstrumentItem> SongInstruments { get; } = new();

      private InstrumentItem selectedSlot;
      /// <summary>The slot (the number a song's VOICE command asks for) that UseInstrument writes to.</summary>
      public InstrumentItem SelectedSlot {
         get => selectedSlot;
         set {
            if (!UpdateReference(ref selectedSlot, value)) return;
            previewSlot?.RaiseCanExecuteChanged();
            useInstrument?.RaiseCanExecuteChanged();
         }
      }

      #endregion

      #region The instrument picker

      public ObservableCollection<InstrumentItem> InstrumentChoices { get; } = new();

      private VoicegroupItem instrumentSourceGroup;
      /// <summary>The voicegroup the picker lists the instruments of.</summary>
      public VoicegroupItem InstrumentSourceGroup {
         get => instrumentSourceGroup;
         set {
            if (!UpdateReference(ref instrumentSourceGroup, value)) return;
            RebuildInstrumentChoices();
         }
      }

      private InstrumentItem selectedInstrument;
      public InstrumentItem SelectedInstrument {
         get => selectedInstrument;
         set {
            if (!UpdateReference(ref selectedInstrument, value)) return;
            previewInstrument?.RaiseCanExecuteChanged();
            useInstrument?.RaiseCanExecuteChanged();
         }
      }

      #endregion

      private VoicegroupItem FindVoicegroupItem(int address) => Voicegroups.FirstOrDefault(group => group.Address == address);

      private VoicegroupItem EnsureVoicegroupItem(int address) {
         var item = FindVoicegroupItem(address);
         if (item != null) return item;
         // a voicegroup nothing else knows about (for example a song whose header points somewhere odd): list it as a custom one
         item = new VoicegroupItem(model, VoicegroupCatalog.Inspect(model, address));
         Voicegroups.Add(item);
         return item;
      }

      private string DescribeVoicegroup(int address) {
         var item = FindVoicegroupItem(address);
         return item != null ? $"{item.Title} ({address:X6})" : $"{address:X6}";
      }

      private int syncedSongIndex = -1;

      /// <summary>
      /// Shows the selected song's voicegroup in every voicegroup control and rebuilds the slot and instrument lists.
      /// The "copy from" and picker voicegroups follow the song when another song is selected, but stay as the user set them while the same song is edited.
      /// </summary>
      private void SyncVoicegroupSelection() {
         var header = selectedSong?.Header;
         var sameSong = selectedSong != null && selectedSong.Index == syncedSongIndex;
         var keepNewSource = sameSong ? newVoicegroupSource?.Address : null;
         var keepPickerSource = sameSong ? instrumentSourceGroup?.Address : null;
         syncedSongIndex = selectedSong?.Index ?? -1;
         updatingVoicegroupSelection = true;
         try {
            var item = header == null ? null : EnsureVoicegroupItem(header.Voicegroup);
            SongVoicegroup = item;
            NewVoicegroupSource = (keepNewSource == null ? null : FindVoicegroupItem(keepNewSource.Value)) ?? item;
            InstrumentSourceGroup = (keepPickerSource == null ? null : FindVoicegroupItem(keepPickerSource.Value)) ?? item;
         } finally {
            updatingVoicegroupSelection = false;
         }
         RebuildSongInstruments();
         RebuildInstrumentChoices();
         UpdateVoicegroupNote();
         NotifyPropertyChanged(nameof(CanChangeVoicegroup));
         RaiseVoicegroupCommandsChanged();
      }

      private void UpdateVoicegroupNote() {
         var item = songVoicegroup;
         if (item == null || selectedSong?.Header == null) { VoicegroupNote = string.Empty; return; }
         var others = OtherSongsUsing(item, selectedSong);
         var sb = new StringBuilder();
         if (item.IsCustom) {
            sb.Append($"The data at {item.Address:X6} doesn't look like a voicegroup. A new voicegroup can still be made as a copy of it.");
         } else if (others.Count > 0) {
            sb.Append($"{others.Count} other song{(others.Count == 1 ? " uses" : "s use")} this voicegroup too ({string.Join(", ", others.Take(4).Select(SongLabel))}{(others.Count > 4 ? ", ..." : "")}). Changing an instrument gives this song its own copy first, so they stay as they are.");
         } else {
            sb.Append("Only this song uses this voicegroup, so changing an instrument changes it in place.");
         }
         if (!item.IsCustom && item.EntryCount < VoicegroupCatalog.MaxEntries) {
            sb.Append($" It holds {item.EntryCount} instruments; a new voicegroup has room for all {VoicegroupCatalog.MaxEntries}.");
         }
         VoicegroupNote = sb.ToString().Trim();
      }

      private static string SongLabel(SongItem song) => string.IsNullOrEmpty(song.Name) ? song.Index.ToString() : song.Name;

      /// <summary>The songs, other than 'song', whose header points at the voicegroup.</summary>
      private List<SongItem> OtherSongsUsing(VoicegroupItem group, SongItem song)
         => Songs.Where(other => other != song && other.Header != null && other.Header.TrackCount > 0 && other.Header.Voicegroup == group.Address).ToList();

      private void RebuildSongInstruments() {
         var keepSlot = selectedSlot?.Slot ?? -1;
         SongInstruments.Clear();
         if (songVoicegroup != null && HasSelectedSong) {
            for (int slot = 0; slot < VoicegroupCatalog.MaxEntries; slot++) {
               var address = songVoicegroup.Address + slot * VoicegroupCatalog.EntrySize;
               var beyondEnd = !songVoicegroup.IsCustom && slot >= songVoicegroup.EntryCount;
               SongInstruments.Add(new InstrumentItem(model, slot, address, beyondEnd));
            }
         }
         SelectedSlot = keepSlot >= 0 && keepSlot < SongInstruments.Count ? SongInstruments[keepSlot] : SongInstruments.FirstOrDefault();
      }

      private int choicesSourceAddress = -1;

      private void RebuildInstrumentChoices() {
         var keepSlot = selectedInstrument?.Slot ?? -1;
         var keepSource = choicesSourceAddress;
         InstrumentChoices.Clear();
         var source = instrumentSourceGroup;
         choicesSourceAddress = source?.Address ?? -1;
         var choices = new List<InstrumentItem>();
         if (source != null && !source.IsCustom) {
            var byBytes = new Dictionary<string, InstrumentItem>();
            for (int slot = 0; slot < Math.Min(source.CopyCount, VoicegroupCatalog.MaxEntries); slot++) {
               var address = source.Address + slot * VoicegroupCatalog.EntrySize;
               var item = new InstrumentItem(model, slot, address, false);
               if (!item.IsValid) continue;
               // many slots hold the very same instrument (the unused ones especially): list each distinct instrument once
               if (byBytes.TryGetValue(item.BytesKey, out var first)) { first.AddSameSlot(slot); continue; }
               byBytes[item.BytesKey] = item;
               choices.Add(item);
            }
         }
         foreach (var choice in choices) InstrumentChoices.Add(choice);
         SelectedInstrument = (keepSource == choicesSourceAddress && keepSlot >= 0 ? choices.FirstOrDefault(choice => choice.Slot == keepSlot) : null) ?? choices.FirstOrDefault();
      }

      /// <summary>After the ROM changed: read the selected song's header again (so the music engine sees the new pointer) and refresh what depends on it.</summary>
      private void RefreshSelectedSongHeader() {
         if (selectedSong == null) return;
         SongHeader.TryRead(model, selectedSong.HeaderAddress, out var header);
         selectedSong.SetHeader(header);
         RecomputeVoicegroupUsage();
         UpdateSongDetails();
      }

      private void RecomputeVoicegroupUsage() {
         var users = new Dictionary<int, List<string>>();
         foreach (var song in Songs) {
            if (song.Header == null || song.Header.TrackCount == 0) continue;
            if (!users.TryGetValue(song.Header.Voicegroup, out var list)) users[song.Header.Voicegroup] = list = new List<string>();
            list.Add(SongLabel(song));
         }
         foreach (var group in Voicegroups) group.UsedBy = users.TryGetValue(group.Address, out var list) ? list : (IReadOnlyList<string>)Array.Empty<string>();
      }

      /// <summary>Re-reads every song and voicegroup (a voicegroup was added) and selects the same song and slot again.</summary>
      private void ReloadSongsKeepingSelection() {
         var index = selectedSong?.Index ?? -1;
         var slot = selectedSlot?.Slot ?? -1;
         var pickerSource = instrumentSourceGroup?.Address;
         LoadSongs();
         SelectedSong = index >= 0 && index < Songs.Count ? Songs[index] : null;
         if (slot >= 0 && slot < SongInstruments.Count) SelectedSlot = SongInstruments[slot];
         var keptPicker = pickerSource == null ? null : FindVoicegroupItem(pickerSource.Value);
         if (keptPicker != null) InstrumentSourceGroup = keptPicker;
      }

      /// <summary>
      /// Voicegroups are found through the songs that use them (and through named anchors). When the song stops using a voicegroup nothing else uses,
      /// give that voicegroup a name, so it stays in the list and the song can go back to it.
      /// </summary>
      private void KeepVoicegroupListed(SongItem song, VoicegroupItem old, ModelDelta token) {
         if (old == null || old.IsCustom || old.Info.AnchorName != null) return;
         if (OtherSongsUsing(old, song).Count > 0) return;
         if (!string.IsNullOrEmpty(model.GetAnchorFromAddress(-1, old.Address))) return; // it has some other name already: that keeps it
         var songName = string.IsNullOrEmpty(song.Name) ? $"song_{song.Index}" : song.Name;
         VoicegroupEditor.NameVoicegroup(model, token, old.Address, VoicegroupEditor.UniqueAnchorName(model, songName + "_original"));
      }

      private void ChangeSongVoicegroup(VoicegroupItem chosen) {
         var song = selectedSong;
         var header = song.Header;
         if (header.Voicegroup == chosen.Address) return;
         var token = viewPort.CurrentChange;
         KeepVoicegroupListed(song, FindVoicegroupItem(header.Voicegroup), token);
         VoicegroupEditor.AssignToSong(model, token, header.Address, chosen.Address);
         viewPort.ChangeHistory.ChangeCompleted();
         viewPort.Refresh();
         RefreshSelectedSongHeader();
         Status = $"{SongLabel(song)} now uses voicegroup {chosen.Title} ({chosen.Address:X6}). Press Play to hear it.";
      }

      private void ExecuteNewVoicegroup() {
         var song = selectedSong;
         if (song?.Header == null) return;
         var source = newVoicegroupSource ?? songVoicegroup;
         var token = viewPort.CurrentChange;
         if (!CreatePrivateVoicegroup(song, source, token, out var address, out var anchorName)) { viewPort.ChangeHistory.ChangeCompleted(); return; }
         viewPort.ChangeHistory.ChangeCompleted();
         viewPort.Refresh();
         ReloadSongsKeepingSelection();
         Status = $"{SongLabel(song)} now has its own voicegroup at {address:X6}{(anchorName == null ? string.Empty : " (" + anchorName + ")")}, a copy of {source?.Title ?? "nothing"}. It sounds the same for now: change its instruments below.";
         OnMessage?.Invoke(this, Status);
      }

      /// <summary>Allocates a new voicegroup (a copy of 'source') and assigns it to the song, all in 'token'. Reports the problem and changes nothing when there is no room.</summary>
      private bool CreatePrivateVoicegroup(SongItem song, VoicegroupItem source, ModelDelta token, out int address, out string anchorName) {
         var name = string.IsNullOrEmpty(song.Name) ? $"song_{song.Index}" : song.Name;
         if (!VoicegroupEditor.TryCreateCopy(model, token, source?.Info, name, out address, out anchorName, out var error)) {
            OnError?.Invoke(this, "Could not make a new voicegroup: " + error);
            return false;
         }
         KeepVoicegroupListed(song, FindVoicegroupItem(song.Header.Voicegroup), token);
         VoicegroupEditor.AssignToSong(model, token, song.Header.Address, address);
         return true;
      }

      private void ExecuteGotoVoicegroup() {
         if (songVoicegroup == null) return;
         viewPort.Goto.Execute(songVoicegroup.Info.AnchorName ?? songVoicegroup.Address.ToString("X6"));
         RequestTabChange?.Invoke(this, new TabChangeRequestedEventArgs(viewPort));
      }

      private void ExecutePreviewSlot() {
         if (selectedSlot == null) return;
         PlayTone(selectedSlot.Address, $"slot {selectedSlot.Slot}");
      }

      private void ExecutePreviewInstrument() {
         if (selectedInstrument == null) return;
         PlayTone(selectedInstrument.Address, $"{instrumentSourceGroup?.Title} #{selectedInstrument.Slot}");
      }

      private void PlayTone(int toneAddress, string what) {
         byte[] wav;
         try {
            wav = new M4aRenderer(model).RenderPreviewNote(toneAddress, PreviewKey, 100);
         } catch (Exception e) {
            OnError?.Invoke(this, "Could not play the instrument: " + e.Message);
            return;
         }
         if (IsSilent(wav)) { Status = $"Nothing to hear for {what} at note {PreviewKey}: the instrument is empty, or has no sound for that note."; return; }
         Status = $"Playing {what} at note {PreviewKey}.";
         RequestPlayWav?.Invoke(this, wav);
      }

      private static bool IsSilent(byte[] wav) {
         for (int i = 44; i + 1 < wav.Length; i += 2) {
            if (Math.Abs((short)(wav[i] | (wav[i + 1] << 8))) > 80) return false;
         }
         return true;
      }

      private void ExecuteUseInstrument() {
         var song = selectedSong;
         if (song?.Header == null || selectedSlot == null || selectedInstrument == null || songVoicegroup == null) return;
         var slot = selectedSlot.Slot;
         var instrument = selectedInstrument;
         var group = songVoicegroup;
         var token = viewPort.CurrentChange;

         // an instrument may only change a voicegroup that belongs to this song and has the slot
         var shared = OtherSongsUsing(group, song).Count > 0;
         var needsCopy = shared || group.IsCustom || slot >= group.EntryCount;
         var targetAddress = group.Address;
         var copyMessage = string.Empty;
         if (needsCopy) {
            if (!CreatePrivateVoicegroup(song, group, token, out targetAddress, out var anchorName)) { viewPort.ChangeHistory.ChangeCompleted(); return; }
            copyMessage = shared
               ? $" {SongLabel(song)} shared its voicegroup with other songs, so it got its own copy ({anchorName ?? targetAddress.ToString("X6")}) first."
               : $" {SongLabel(song)} now has its own voicegroup ({anchorName ?? targetAddress.ToString("X6")}) with room for every slot.";
         }
         if (!VoicegroupEditor.TryCopyTone(model, token, targetAddress, slot, instrument.Address, out var error)) {
            viewPort.ChangeHistory.ChangeCompleted();
            OnError?.Invoke(this, error);
            return;
         }
         viewPort.ChangeHistory.ChangeCompleted();
         viewPort.Refresh();
         if (needsCopy) ReloadSongsKeepingSelection(); else { RefreshSelectedSongHeader(); }
         if (slot < SongInstruments.Count) SelectedSlot = SongInstruments[slot];
         Status = $"Slot {slot} now plays {instrument.Description} (from {instrumentSourceGroup?.Title} #{instrument.Slot}).{copyMessage} Press Play to hear it in the song.";
         OnMessage?.Invoke(this, Status);
      }

      #endregion
   }

   public class CryItem : ViewModelCore {
      public int Index { get; }
      public string Name { get; }
      public int EntryAddress { get; }
      public int SampleAddress { get; }
      public GbaSample Sample { get; }
      public string Label => string.IsNullOrEmpty(Name) ? Index.ToString() : $"{Index} {Name}";
      public string Summary => Sample == null ? "(invalid)" : $"{Sample.SampleRate:0} Hz, {Sample.DurationSeconds:0.00} s";

      private bool isFilteredOut;
      public bool IsFilteredOut { get => isFilteredOut; set => TryUpdate(ref isFilteredOut, value); }
      private bool selected;
      public bool Selected { get => selected; set => TryUpdate(ref selected, value); }

      public CryItem(int index, string name, int entryAddress, int sampleAddress, GbaSample sample) {
         (Index, Name, EntryAddress, SampleAddress, Sample) = (index, name ?? string.Empty, entryAddress, sampleAddress, sample);
      }

      public void MatchToFilter(string filter) {
         if (string.IsNullOrWhiteSpace(filter)) { IsFilteredOut = false; return; }
         var terms = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
         IsFilteredOut = !terms.All(term => Index.ToString() == term || Name.Contains(term, StringComparison.OrdinalIgnoreCase));
      }
   }

   public class SongItem : ViewModelCore {
      public int Index { get; }
      private string name;
      public string Name {
         get => name;
         set { if (!TryUpdate(ref name, value ?? string.Empty)) return; NotifyPropertyChanged(nameof(Label)); }
      }
      public int EntryAddress { get; }
      public int HeaderAddress { get; }
      public SongHeader Header { get; private set; }
      public int MusicPlayer { get; }
      public string Label => string.IsNullOrEmpty(Name) ? Index.ToString() : $"{Index} {Name}";

      /// <summary>The ROM was edited (for example the song got another voicegroup): use the header as it reads now.</summary>
      public void SetHeader(SongHeader header) {
         Header = header;
         NotifyPropertyChanged(nameof(Header));
         NotifyPropertyChanged(nameof(Summary));
      }

      public string Summary => Header == null ? "(invalid)" : Header.TrackCount == 0 ? "no tracks" : $"{Header.TrackCount} track{(Header.TrackCount == 1 ? "" : "s")}";

      private bool isFilteredOut;
      public bool IsFilteredOut { get => isFilteredOut; set => TryUpdate(ref isFilteredOut, value); }
      private bool selected;
      public bool Selected { get => selected; set => TryUpdate(ref selected, value); }

      public SongItem(int index, string name, int entryAddress, int headerAddress, SongHeader header, int musicPlayer) {
         (Index, this.name, EntryAddress, HeaderAddress, Header, MusicPlayer) = (index, name ?? string.Empty, entryAddress, headerAddress, header, musicPlayer);
      }

      public void MatchToFilter(string filter) {
         if (string.IsNullOrWhiteSpace(filter)) { IsFilteredOut = false; return; }
         var terms = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
         IsFilteredOut = !terms.All(term => Index.ToString() == term || Name.Contains(term, StringComparison.OrdinalIgnoreCase));
      }
   }

   /// <summary>A voicegroup in a drop-down: its name (from the metadata when there is one), size and the songs that use it.</summary>
   public class VoicegroupItem : ViewModelCore {
      private readonly IDataModel model;
      private IReadOnlyList<string> usedBy;

      public VoicegroupInfo Info { get; }
      public int Address => Info.Address;
      public int EntryCount => Info.EntryCount;
      public int CopyCount => Info.CopyCount;
      /// <summary>True when the address isn't recognizable as a voicegroup (there are no valid instruments).</summary>
      public bool IsCustom => Info.IsCustom;

      /// <summary>"route110" when the metadata names the voicegroup, otherwise "Voicegroup 1351990" or, for something that isn't a voicegroup, "Custom (1351990)".</summary>
      public string Title => Info.IsCustom ? $"Custom ({Info.Address:X6})" : Info.Name ?? $"Voicegroup {Info.Address:X6}";

      public IReadOnlyList<string> UsedBy {
         get => usedBy;
         set {
            usedBy = value ?? Array.Empty<string>();
            NotifyPropertyChanged();
            NotifyPropertyChanged(nameof(Label));
            NotifyPropertyChanged(nameof(Description));
         }
      }

      public string Label {
         get {
            var sb = new StringBuilder(Title);
            if (!Info.IsCustom) sb.Append($" · {Info.EntryCount} instruments");
            sb.Append(usedBy.Count == 0 ? " · unused" : $" · used by {usedBy[0]}{(usedBy.Count > 1 ? $" +{usedBy.Count - 1}" : string.Empty)}");
            return sb.ToString();
         }
      }

      /// <summary>The tooltip: where it is, what its first instruments are and who uses it.</summary>
      public string Description {
         get {
            var sb = new StringBuilder($"{Title} at {Address:X6}");
            if (Info.AnchorName != null && Info.Name != Info.AnchorName) sb.Append($" ({Info.AnchorName})");
            sb.AppendLine();
            if (Info.IsCustom) sb.AppendLine("Not recognizable as a voicegroup.");
            else sb.AppendLine($"{Info.EntryCount} instruments, starting with: {VoicegroupCatalog.DescribeStart(model, Address, Info.EntryCount)}");
            sb.Append(usedBy.Count == 0 ? "No song uses it." : $"Used by: {string.Join(", ", usedBy.Take(8))}{(usedBy.Count > 8 ? $" and {usedBy.Count - 8} more" : string.Empty)}");
            return sb.ToString();
         }
      }

      public VoicegroupItem(IDataModel model, VoicegroupInfo info) {
         this.model = model;
         Info = info;
         usedBy = info.UsedBy ?? Array.Empty<string>();
      }

      public override string ToString() => Label;
   }

   /// <summary>One slot of a voicegroup (or one distinct instrument of it): the 12 byte ToneData entry a song's VOICE command selects.</summary>
   public class InstrumentItem : ViewModelCore {
      private readonly List<int> sameSlots = new();

      public int Slot { get; }
      public int Address { get; }
      public ToneData Tone { get; }
      public bool IsBeyondEnd { get; }
      public bool IsValid => Tone != null && Tone.IsValid;
      /// <summary>The 12 bytes as hex: two instruments with the same key sound the same.</summary>
      public string BytesKey { get; }
      /// <summary>What the instrument is: "Sample 5.2 KB, 13379 Hz, 0.39 s", "Square wave 1, 50% duty", ...</summary>
      public string Description { get; }

      public string Label {
         get {
            var also = sameSlots.Count == 0 ? string.Empty : $"  (also {string.Join(", ", sameSlots.Take(4).Select(slot => "#" + slot))}{(sameSlots.Count > 4 ? ", ..." : string.Empty)})";
            return $"#{Slot}  {Description}{(IsBeyondEnd ? "  (past the end of this voicegroup)" : string.Empty)}{also}";
         }
      }

      public InstrumentItem(IDataModel model, int slot, int address, bool beyondEnd) {
         (Slot, Address, IsBeyondEnd) = (slot, address, beyondEnd);
         Tone = ToneData.Read(model, address);
         Description = Tone == null ? "(outside the ROM)" : Tone.Describe(model);
         BytesKey = Tone == null ? string.Empty : string.Concat(Enumerable.Range(0, ToneData.Size).Select(i => model[address + i].ToString("X2")));
      }

      /// <summary>Another slot of the same voicegroup holds this very instrument.</summary>
      public void AddSameSlot(int slot) {
         sameSlots.Add(slot);
         NotifyPropertyChanged(nameof(Label));
      }

      public override string ToString() => Label;
   }
}
