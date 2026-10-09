using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Map;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.Models.Runs.Sprites;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using HavenSoft.HexManiac.Core.ViewModels.Images;
using HavenSoft.HexManiac.Core.ViewModels.Map;
using HexManiac.Core.Models.Runs.Sprites;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;

namespace HavenSoft.HexManiac.Core.ViewModels.Tools {
   ///// <summary>
   ///// Sometimes notifying after every change is too noisy.
   ///// Custom <see cref="INotifyCollectionChanged"/> implementation that allows delayed notifications.
   ///// </summary>
   public class ObservableList<T> : List<T>, INotifyCollectionChanged {
      public event NotifyCollectionChangedEventHandler? CollectionChanged;

      public ObservableList() : base() { }
      public ObservableList(IEnumerable<T> items) : base(items) { }

      public void RaiseRefresh() => CollectionChanged?.Invoke(this, new(NotifyCollectionChangedAction.Reset));
   }

   public class TableGroupViewModel : ViewModelCore {
      public const string DefaultName = "Other";

      private bool isOpen;
      private int currentMember; // used with open/close when refreshing the collection

      private string groupName;
      public bool DisplayHeader => GroupName != DefaultName;
      public string GroupName { get => groupName; set => Set(ref groupName, value, old => NotifyPropertyChanged(nameof(DisplayHeader))); }

      private readonly ViewPort viewPort;

      public ObservableCollection<IArrayElementViewModel> Members { get; } = new();

      public Action<IStreamArrayElementViewModel> ForwardModelChanged { get; init; }
      public Action<IStreamArrayElementViewModel> ForwardModelDataMoved { get; init; }

      public TableGroupViewModel(ViewPort viewPort) { GroupName = DefaultName; this.viewPort = viewPort; }

      public bool IsOpen => isOpen;

      public void Open() {
         if (isOpen) return;
         currentMember = 0;
         isOpen = true;
      }

      private bool useMultiFieldFeature = false;
      public bool UseMultiFieldFeature { get => useMultiFieldFeature; set => Set(ref useMultiFieldFeature, value); }

      private MultiFieldArrayElementViewModel multiInProgress;

      public void Add(IArrayElementViewModel child) {
         if (UseMultiFieldFeature && child is IMultiEnabledArrayElementViewModel newField) {
            if (multiInProgress == null) multiInProgress = new(viewPort);
            multiInProgress.Add(newField);
         } else if (currentMember == Members.Count) {
            if (multiInProgress != null) {
               Members.Add(multiInProgress);
               multiInProgress = null;
               currentMember++;
            }
            Members.Add(child);
            currentMember++;
         } else {
            if (multiInProgress != null) {
               if (Members[currentMember].TryCopy(multiInProgress)) {
                  // no need to copy
               } else {
                  Members[currentMember] = multiInProgress;
               }
               multiInProgress = null;
               currentMember++;
               Add(child); // we're adding a non-multi, and now have dealt with the current multi. Recurse so the child can be added using either "full" or "replace" strategy
            } else if (Members[currentMember].TryCopy(child)) {
               currentMember += 1; // copied over successfully
            } else {
               // replace existing
               Members[currentMember] = child;
               currentMember += 1;
            }
         }
      }

      public void Close() {
         if (!isOpen) return;
         if (multiInProgress != null) {
            if (currentMember == Members.Count) {
               Members.Add(multiInProgress);
            } else if (!Members[currentMember].TryCopy(multiInProgress)) {
               Members[currentMember] = multiInProgress;
            }
            currentMember += 1;
            multiInProgress = null;
         }
         while (Members.Count > currentMember) Members.RemoveAt(Members.Count - 1);
         isOpen = false;
      }

      /// <param name="splitPortion">Only add the fields of this partition (partitions are separated by '|' in the table's format). -1 adds every field.</param>
      /// <param name="fields">If not empty, add just these fields (names, or partition numbers) in this order, instead of the partition: this is how a group re-orders a table.</param>
      public void AddChildrenFromTable(ViewPort viewPort, Selection selection, ITableRun table, int index, SplitterArrayElementViewModel header, TableGroupViewModel helperGroup, int splitPortion = -1, IReadOnlyList<string> fields = null) {
         var itemAddress = table.Start + table.ElementLength * index;
         var originalItemAddress = itemAddress;

         foreach (var (item, address) in SelectFields(viewPort, table, itemAddress, splitPortion, fields)) {
            itemAddress = address;
            IArrayElementViewModel viewModel = null;
            if (item.Type == ElementContentType.Unknown) viewModel = new FieldArrayElementViewModel(viewPort, item.Name, itemAddress, item.Length, HexFieldStrategy.Instance);
            else if (item.Type == ElementContentType.PCS) viewModel = new FieldArrayElementViewModel(viewPort, item.Name, itemAddress, item.Length, new TextFieldStrategy());
            else if (item.Type == ElementContentType.Pointer) viewModel = new FieldArrayElementViewModel(viewPort, item.Name, itemAddress, item.Length, new AddressFieldStrategy());
            else if (item.Type == ElementContentType.BitArray) viewModel = new BitListArrayElementViewModel(viewPort, item.Name, itemAddress);
            else if (item.Type == ElementContentType.Integer) {
               if (item is ArrayRunEnumSegment enumSegment) {
                  viewModel = new ComboBoxArrayElementViewModel(viewPort, selection, item.Name, itemAddress, item.Length);
                  var anchor = viewPort.Model.GetAnchorFromAddress(-1, table.Start);
                  var enumSourceTableStart = viewPort.Model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, enumSegment.EnumName);
                  if (!string.IsNullOrEmpty(anchor)) {
                     var dependentArrays = viewPort.Model.GetDependantArrays(anchor).ToList();
                     if (dependentArrays.Count == 1 && enumSourceTableStart >= 0 && dependentArrays[0].ElementContent[0] is ArrayRunBitArraySegment) {
                        Add(viewModel);
                        viewModel = new BitListArrayElementViewModel(viewPort, item.Name, itemAddress);
                     }
                  }
               } else if (item is ArrayRunTupleSegment tupleItem) {
                  viewModel = new TupleArrayElementViewModel(viewPort, tupleItem, itemAddress);
               } else if (item is ArrayRunHexSegment) {
                  viewModel = new FieldArrayElementViewModel(viewPort, item.Name, itemAddress, item.Length, HexFieldStrategy.Instance);
               } else if (item is ArrayRunColorSegment) {
                  viewModel = new ColorFieldArrayElementViewModel(viewPort, item.Name, itemAddress);
               } else if (item is ArrayRunCalculatedSegment calcSeg) {
                  viewModel = new CalculatedElementViewModel(viewPort, calcSeg, originalItemAddress);
               } else if (item is ArrayRunPythonButtonSegment pythonButton) {
                  viewModel = new PythonButtonElementViewModel(viewPort, pythonButton, originalItemAddress);
               } else if (item is ArrayRunOffsetRenderSegment renderSeg) {
                  viewModel = new OffsetRenderViewModel(viewPort, renderSeg, itemAddress);
               } else if (item is ArrayRunSignedSegment signedSegment) {
                  viewModel = new FieldArrayElementViewModel(viewPort, item.Name, itemAddress, item.Length, SignedFieldStrategy.Instance);
               } else {
                  viewModel = new FieldArrayElementViewModel(viewPort, item.Name, itemAddress, item.Length, new NumericFieldStrategy());
               }
            } else {
               throw new NotImplementedException();
            }
            if (!item.IsUnused() && viewModel is not null) {
               Add(viewModel);
               helperGroup.AddChildrenFromPointerSegment(viewPort, itemAddress, item, viewModel, header, recursionLevel: 0);
            }
         }
         AddAdhocSpecialElementsToGroup(viewPort, table);
      }

      /// <summary>
      /// Works out which fields of one table element to show, with the address of each, in the order to show them.
      /// By default that is memory order, limited to one partition of the table. With a field list, it is the order of the list.
      /// </summary>
      public static IReadOnlyList<(ArrayRunElementSegment item, int address)> SelectFields(ViewPort viewPort, ITableRun table, int elementAddress, int splitPortion, IReadOnlyList<string> fields) {
         // first, where does every field live? ('|' splitters take no space: they only start the next partition)
         var layout = new List<(ArrayRunElementSegment item, int address, int partition)>();
         var itemAddress = elementAddress;
         var currentPartition = 0;
         foreach (var itemSegment in table.ElementContent) {
            var item = itemSegment;
            if (item is ArrayRunRecordSegment recordItem) item = recordItem.CreateConcrete(viewPort.Model, table, itemAddress);
            if (itemSegment is ArrayRunSplitterSegment) {
               currentPartition += 1;
               continue;
            }
            layout.Add((item, itemAddress, currentPartition));
            itemAddress += item.Length;
         }

         var result = new List<(ArrayRunElementSegment item, int address)>();
         if (fields == null || fields.Count == 0) {
            foreach (var entry in layout) {
               if (splitPortion != -1 && splitPortion != entry.partition) continue;
               result.Add((entry.item, entry.address));
            }
            return result;
         }

         var used = new HashSet<int>(); // indexes into layout: a field is only shown once, even if the list mentions it twice
         foreach (var field in fields) {
            var isPartition = int.TryParse(field, out var partition);
            for (int i = 0; i < layout.Count; i++) {
               var matches = isPartition ? layout[i].partition == partition : layout[i].item.Name.Equals(field, StringComparison.OrdinalIgnoreCase);
               if (matches && used.Add(i)) result.Add((layout[i].item, layout[i].address));
            }
         }
         return result;
      }

      private void AddAdhocSpecialElementsToGroup(IViewPort viewPort, ITableRun table) {
         if (!viewPort.Model.IsEmerald() && table.ElementContent.Count == 14 && table.ElementCount == 1 && table.ElementContent.Select(seg => seg.Type).ToArray().SequenceEqual(new[] {
            ElementContentType.Pointer, ElementContentType.Pointer, ElementContentType.Pointer, ElementContentType.Pointer,
            ElementContentType.Integer, ElementContentType.Integer, ElementContentType.Integer, ElementContentType.Integer,
            ElementContentType.Integer, ElementContentType.Integer, ElementContentType.Integer, ElementContentType.Integer,
            ElementContentType.Integer, ElementContentType.Integer,
         })) {
            AddAdhocMapElementsToGroup(viewPort, table);
         }

         if (viewPort.Model.IsEmerald() && table.ElementContent.Count == 13 && table.ElementCount == 1 && table.ElementContent.Select(seg => seg.Type).ToArray().SequenceEqual(new[] {
            ElementContentType.Pointer, ElementContentType.Pointer, ElementContentType.Pointer, ElementContentType.Pointer,
            ElementContentType.Integer, ElementContentType.Integer, ElementContentType.Integer, ElementContentType.Integer,
            ElementContentType.Integer, ElementContentType.Integer, ElementContentType.Integer, ElementContentType.Integer,
            ElementContentType.Integer,
         })) {
            AddAdhocMapElementsToGroup(viewPort, table);
         }
      }

      private void AddAdhocMapElementsToGroup(IViewPort viewPort, ITableRun table) {
         var model = viewPort.Model;
         if (table.PointerSources == null || table.PointerSources.Count != 1) return;
         var addressInBankTable = table.PointerSources[0];
         var bankTable = model.GetNextRun(addressInBankTable) as ITableRun;
         if (bankTable == null || bankTable.PointerSources == null || bankTable.PointerSources.Count != 1) return;
         var topTable = model.GetNextRun(bankTable.PointerSources[0]) as ITableRun;
         if (topTable == null) return;
         var bankOffset = topTable.ConvertByteOffsetToArrayOffset(bankTable.PointerSources[0]);
         var mapOffset = bankTable.ConvertByteOffsetToArrayOffset(addressInBankTable);
         var (bank, map) = (bankOffset.ElementIndex, mapOffset.ElementIndex);
         var name = "maps.bank" + bank + BlockMapViewModel.MapIDToText(model, bank, map);
         var matches = model.GetMatchingMaps(name);
         if (matches.Count != 1) return;
         var mapModel = new MapModel(new ModelArrayElement(model, table.Start, 0, () => viewPort.ChangeHistory.CurrentChange, table));
         if (model.GetNextRun(mapModel.Layout.BlockMap.Start) is BlockmapRun blockmapRun) {
            var image = (CanvasPixelViewModel)model.CurrentCacheScope.GetImage(blockmapRun);
            image.SpriteScale = 128.0 / Math.Max(image.PixelWidth, image.PixelHeight);
            Add(new SpriteIndicatorElementViewModel(image));
         }
         Add(new ButtonArrayElementViewModel("Edit Map", () => viewPort.Goto.Execute(name)));
      }

      /// <summary>
      /// Most streams are edited as text. A list of encounters (min level, max level, species) gets a row of boxes per element instead.
      /// </summary>
      private static TextStreamElementViewModel CreateTextStream(ViewPort viewPort, IFormattedRun streamRun, string name, int address, string format) {
         if (streamRun is ITableRun table && TableRowsStreamElementViewModel.Supports(table)) return new TableRowsStreamElementViewModel(viewPort, name, address, format);
         return new TextStreamElementViewModel(viewPort, name, address, format);
      }

      private void AddChildrenFromPointerSegment(ViewPort viewPort, int itemAddress, ArrayRunElementSegment item, IArrayElementViewModel parent, SplitterArrayElementViewModel header, int recursionLevel) {
         if (!(item is ArrayRunPointerSegment pointerSegment)) return;
         if (pointerSegment.InnerFormat == string.Empty) return;
         var destination = viewPort.Model.ReadPointer(itemAddress);
         IFormattedRun streamRun = null;
         if (destination != Pointer.NULL) {
            streamRun = viewPort.Model.GetNextRun(destination);
            if (!pointerSegment.DestinationDataMatchesPointerFormat(viewPort.Model, new NoDataChangeDeltaModel(), itemAddress, destination, null, -1)) streamRun = null;
            if (streamRun != null && streamRun.Start != destination) {
               // For some reason (possibly because of a run length conflict),
               //    the destination data appears to match the expected type,
               //    but there is no run for it.
               // Go ahead and generate a new temporary run for the data.
               var strategy = viewPort.Model.FormatRunFactory.GetStrategy(pointerSegment.InnerFormat);
               strategy.TryParseData(viewPort.Model, string.Empty, destination, ref streamRun);
            }
         }

         // Pick the most specific view model first. LZ sprites are also IStreamRuns, and building a text view model for one
         // serializes the whole compressed image as text before it gets thrown away: with a dozen sprites per table element
         // (decomp-style species structs) that was most of the time spent switching between elements.
         IStreamArrayElementViewModel streamElement = null;
         var parentStart = parent is StreamElementViewModel streamParent ? streamParent.Start : -1;
         if (streamRun is ITrainerTeamRun tptRun) streamElement = new TrainerPokemonTeamElementViewModel(viewPort, tptRun, item.Name, itemAddress);
         else if (streamRun is IPaletteRun paletteRun) streamElement = new PaletteElementViewModel(viewPort, viewPort.ChangeHistory, item.Name, paletteRun.FormatString, paletteRun.PaletteFormat, itemAddress);
         else if (streamRun is ISpriteRun spriteRun) streamElement = new SpriteElementViewModel(viewPort, item.Name, spriteRun.FormatString, spriteRun.SpriteFormat, itemAddress);
         else if (streamRun == null || streamRun is IStreamRun || streamRun is ITableRun) streamElement = CreateTextStream(viewPort, streamRun, item.Name, itemAddress, pointerSegment.InnerFormat);
         if (streamElement == null) return;
         streamElement.Parent = header;

         var streamAddress = itemAddress;
         var myIndex = currentMember;
         parent.DataChanged += (sender, e) => {
            var closure_destination = viewPort.Model.ReadPointer(streamAddress);
            var run = viewPort.Model.GetNextRun(closure_destination) as IStreamRun;
            IStreamArrayElementViewModel newStream = null;

            var parentStart = parent is StreamElementViewModel streamParent ? streamParent.Start : -1;
            if (run is IPaletteRun paletteRun1) newStream = new PaletteElementViewModel(viewPort, viewPort.ChangeHistory, item.Name, paletteRun1.FormatString, paletteRun1.PaletteFormat, streamAddress);
            else if (run is ISpriteRun spriteRun1) newStream = new SpriteElementViewModel(viewPort, item.Name, spriteRun1.FormatString, spriteRun1.SpriteFormat, streamAddress);
            else if (run == null || run is IStreamRun) newStream = CreateTextStream(viewPort, run, item.Name, streamAddress, pointerSegment.InnerFormat);

            ForwardModelChanged(newStream);
            ForwardModelDataMoved(newStream);
            // using var scope = Members[myIndex].SilencePropertyNotifications();
            if (!Members[myIndex].TryCopy(newStream)) Members[myIndex] = newStream;
         };
         ForwardModelDataMoved(streamElement);
         Add(streamElement);

         if (streamRun is ITableRun tableRun && recursionLevel < 1) {
            int segmentOffset = 0;
            for (int i = 0; i < tableRun.ElementContent.Count; i++) {
               if (!(tableRun.ElementContent[i] is ArrayRunPointerSegment)) { segmentOffset += tableRun.ElementContent[i].Length; continue; }
               for (int j = 0; j < tableRun.ElementCount; j++) {
                  itemAddress = tableRun.Start + segmentOffset + j * tableRun.ElementLength;
                  AddChildrenFromPointerSegment(viewPort, itemAddress, tableRun.ElementContent[i], streamElement, header, recursionLevel + 1);
               }
               segmentOffset += tableRun.ElementContent[i].Length;
            }
         }
      }
   }
}
