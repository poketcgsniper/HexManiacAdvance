using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.Models.Sound;
using HavenSoft.HexManiac.Core.ViewModels.Images;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
         if (file.Name.EndsWith(".s", StringComparison.OrdinalIgnoreCase)) { InsertSongFromFile(file); return true; }
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
         Undo = viewPort.Undo;
         Redo = viewPort.Redo;
         close.CanExecute = arg => true;
         close.Execute = arg => Closed?.Invoke(this, EventArgs.Empty);
         Reload();
      }

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
         var newAddress = model.FindFreeSpace(model.FreeSpaceStart, bytes.Length);
         if (newAddress < 0) {
            newAddress = model.Count;
            model.ExpandData(token, model.Count + bytes.Length);
         }
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
            UpdateSongDetails();
         }
      }
      public bool HasSelectedSong => selectedSong?.Header != null;

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

      private int insertMusicPlayer;
      public int InsertMusicPlayer { get => insertMusicPlayer; set => Set(ref insertMusicPlayer, value.LimitToRange(0, 3)); }

      private StubCommand exportSong, insertSong, gotoSong;
      public ICommand ExportSong => StubCommand(ref exportSong, ExecuteExportSong, () => HasSelectedSong);
      public ICommand InsertSong => StubCommand(ref insertSong, ExecuteInsertSong, () => HasSongs);
      public ICommand GotoSong => StubCommand(ref gotoSong, ExecuteGotoSong, () => selectedSong != null);

      private void LoadSongs() {
         Songs.Clear();
         Voicegroups.Clear();
         var table = model.GetTable(SongTable);
         if (table == null) return;
         IReadOnlyList<string> names = model.GetOptions(SongTable);
         if ((names == null || names.Count == 0) && model.TryGetList(SongNamesList, out var songNames)) names = songNames;
         var voicegroupUsers = new Dictionary<int, List<string>>();
         for (int i = 0; i < table.ElementCount; i++) {
            var entry = table.Start + table.ElementLength * i;
            var headerAddress = model.ReadPointer(entry);
            SongHeader.TryRead(model, headerAddress, out var header);
            var name = names != null && i < names.Count ? names[i] : string.Empty;
            var musicPlayer = model.ReadMultiByteValue(entry + 4, 2);
            Songs.Add(new SongItem(i, name, entry, headerAddress, header, musicPlayer));
            if (header != null && header.TrackCount > 0) {
               if (!voicegroupUsers.TryGetValue(header.Voicegroup, out var users)) voicegroupUsers[header.Voicegroup] = users = new List<string>();
               users.Add(string.IsNullOrEmpty(name) ? i.ToString() : name);
            }
         }
         foreach (var pair in voicegroupUsers.OrderBy(pair => pair.Key)) {
            Voicegroups.Add(new VoicegroupItem(pair.Key, pair.Value));
         }
         foreach (var item in Songs) item.MatchToFilter(songFilter);
         if (Voicegroups.Count > 0) SelectedVoicegroup = Voicegroups[0];
      }

      private void UpdateSongDetails() {
         if (selectedSong == null) { SongDetails = string.Empty; SongText = string.Empty; return; }
         var sb = new StringBuilder();
         sb.AppendLine($"Song {selectedSong.Index}{(string.IsNullOrEmpty(selectedSong.Name) ? "" : ": " + selectedSong.Name)}");
         sb.AppendLine($"Table entry: {SongTable}/{selectedSong.Index} ({selectedSong.EntryAddress:X6}), music player {selectedSong.MusicPlayer}");
         if (selectedSong.Header == null) {
            sb.AppendLine($"Header at {selectedSong.HeaderAddress:X6} is not a valid song header.");
            SongDetails = sb.ToString();
            SongText = string.Empty;
            return;
         }
         var header = selectedSong.Header;
         sb.AppendLine($"Header: {header.Address:X6}, {header.TrackCount} tracks, priority {header.Priority}, reverb {header.Reverb}");
         sb.AppendLine($"Voicegroup: {header.Voicegroup:X6}");
         SongDetails = sb.ToString();
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
         var name = fileSystem.RequestNewName(defaultName + ".s", "Song assembly (Sappy / mid2agb)", "s");
         if (string.IsNullOrEmpty(name)) return;
         if (fileSystem.Save(new LoadedFile(name, Encoding.UTF8.GetBytes(SongText)))) Status = $"Exported song {selectedSong.Index} to {name}";
      }

      private void ExecuteInsertSong() {
         var file = fileSystem.OpenFile("Song assembly (Sappy / mid2agb)", "s", "asm", "txt");
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
         var address = model.FindFreeSpace(model.FreeSpaceStart, result.Bytes.Length);
         if (address < 0) {
            address = model.Count;
            model.ExpandData(token, model.Count + result.Bytes.Length);
         }
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
         Status = $"Inserted {file.Name} ({result.SongName}, {bytes.Length} bytes) at {address:X6} as song {index}.";
         OnMessage?.Invoke(this, Status + (InsertAsNewSong ? $" Play it with 'playbgm {index}' / 'playse {index}'." : string.Empty));
      }

      private void ExecuteGotoSong() {
         if (selectedSong == null) return;
         viewPort.Goto.Execute($"{SongTable}/{selectedSong.Index}");
         RequestTabChange?.Invoke(this, new TabChangeRequestedEventArgs(viewPort));
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
      public string Name { get; }
      public int EntryAddress { get; }
      public int HeaderAddress { get; }
      public SongHeader Header { get; }
      public int MusicPlayer { get; }
      public string Label => string.IsNullOrEmpty(Name) ? Index.ToString() : $"{Index} {Name}";
      public string Summary => Header == null ? "(invalid)" : Header.TrackCount == 0 ? "no tracks" : $"{Header.TrackCount} track{(Header.TrackCount == 1 ? "" : "s")}";

      private bool isFilteredOut;
      public bool IsFilteredOut { get => isFilteredOut; set => TryUpdate(ref isFilteredOut, value); }
      private bool selected;
      public bool Selected { get => selected; set => TryUpdate(ref selected, value); }

      public SongItem(int index, string name, int entryAddress, int headerAddress, SongHeader header, int musicPlayer) {
         (Index, Name, EntryAddress, HeaderAddress, Header, MusicPlayer) = (index, name ?? string.Empty, entryAddress, headerAddress, header, musicPlayer);
      }

      public void MatchToFilter(string filter) {
         if (string.IsNullOrWhiteSpace(filter)) { IsFilteredOut = false; return; }
         var terms = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
         IsFilteredOut = !terms.All(term => Index.ToString() == term || Name.Contains(term, StringComparison.OrdinalIgnoreCase));
      }
   }

   public class VoicegroupItem {
      public int Address { get; }
      public IReadOnlyList<string> UsedBy { get; }
      public string Label => $"{Address:X6} ({UsedBy[0]}{(UsedBy.Count > 1 ? $" +{UsedBy.Count - 1} more" : "")})";
      public VoicegroupItem(int address, IReadOnlyList<string> usedBy) => (Address, UsedBy) = (address, usedBy);
      public override string ToString() => Label;
   }
}
