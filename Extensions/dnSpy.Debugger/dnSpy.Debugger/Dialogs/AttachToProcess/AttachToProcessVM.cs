﻿/*
    Copyright (C) 2014-2017 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.Settings.AppearanceCategory;
using dnSpy.Contracts.Text.Classification;
using dnSpy.Contracts.Utilities;
using dnSpy.Debugger.Properties;
using dnSpy.Debugger.Text;
using dnSpy.Debugger.UI;
using dnSpy.Debugger.Utilities;
using Microsoft.VisualStudio.Text.Classification;

namespace dnSpy.Debugger.Dialogs.AttachToProcess {
	sealed class AttachToProcessVM : ViewModelBase {
		readonly ObservableCollection<ProgramVM> realAllItems;
		public BulkObservableCollection<ProgramVM> AllItems { get; }
		public ObservableCollection<ProgramVM> SelectedItems { get; }

		public string SearchToolTip => ToolTipHelper.AddKeyboardShortcut(dnSpy_Debugger_Resources.AttachToProcess_Search_ToolTip, dnSpy_Debugger_Resources.ShortCutKeyCtrlF);

		public ICommand RefreshCommand => new RelayCommand(a => Refresh(), a => CanRefresh);

		public string Title {
			get {
				if (!Environment.Is64BitOperatingSystem)
					return dnSpy_Debugger_Resources.Attach_AttachToProcess;
				return IntPtr.Size == 4 ? dnSpy_Debugger_Resources.Attach_AttachToProcess32 :
						dnSpy_Debugger_Resources.Attach_AttachToProcess64;
			}
		}

		public bool HasDebuggingText => Environment.Is64BitOperatingSystem;
		public string DebuggingText => IntPtr.Size == 4 ? dnSpy_Debugger_Resources.Attach_UseDnSpy32 : dnSpy_Debugger_Resources.Attach_UseDnSpy64;

		public string FilterText {
			get => filterText;
			set {
				if (filterText == value)
					return;
				filterText = value;
				OnPropertyChanged(nameof(FilterText));
				FilterList(filterText);
			}
		}
		string filterText = string.Empty;

		readonly UIDispatcher uiDispatcher;
		readonly DbgManager dbgManager;
		readonly AttachProgramOptionsAggregatorFactory attachProgramOptionsAggregatorFactory;
		readonly AttachToProcessContext attachToProcessContext;
		AttachProgramOptionsAggregator attachProgramOptionsAggregator;
		ProcessProvider processProvider;

		public AttachToProcessVM(UIDispatcher uiDispatcher, DbgManager dbgManager, DebuggerSettings debuggerSettings, ProgramFormatterProvider programFormatterProvider, IClassificationFormatMapService classificationFormatMapService, ITextElementProvider textElementProvider, AttachProgramOptionsAggregatorFactory attachProgramOptionsAggregatorFactory) {
			realAllItems = new ObservableCollection<ProgramVM>();
			AllItems = new BulkObservableCollection<ProgramVM>();
			SelectedItems = new ObservableCollection<ProgramVM>();
			this.uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
			uiDispatcher.VerifyAccess();
			this.dbgManager = dbgManager ?? throw new ArgumentNullException(nameof(dbgManager));
			this.attachProgramOptionsAggregatorFactory = attachProgramOptionsAggregatorFactory ?? throw new ArgumentNullException(nameof(attachProgramOptionsAggregatorFactory));
			var classificationFormatMap = classificationFormatMapService.GetClassificationFormatMap(AppearanceCategoryConstants.UIMisc);
			attachToProcessContext = new AttachToProcessContext(classificationFormatMap, textElementProvider, new SearchMatcher());

			attachToProcessContext.Formatter = programFormatterProvider.Create();
			attachToProcessContext.SyntaxHighlight = debuggerSettings.SyntaxHighlight;

			RefreshCore();
		}

		public bool IsRefreshing => !CanRefresh;
		bool CanRefresh => attachProgramOptionsAggregator == null;
		void Refresh() {
			uiDispatcher.VerifyAccess();
			if (!CanRefresh)
				return;
			RefreshCore();
		}

		void RefreshCore() {
			uiDispatcher.VerifyAccess();
			RemoveAggregator();
			ClearAllItems();
			processProvider = new ProcessProvider();
			attachProgramOptionsAggregator = attachProgramOptionsAggregatorFactory.Create();
			attachProgramOptionsAggregator.AttachProgramOptionsAdded += AttachProgramOptionsAggregator_AttachProgramOptionsAdded;
			attachProgramOptionsAggregator.Completed += AttachProgramOptionsAggregator_Completed;
			attachProgramOptionsAggregator.Start();
			OnPropertyChanged(nameof(IsRefreshing));
		}

		void RemoveAggregator() {
			uiDispatcher.VerifyAccess();
			processProvider?.Dispose();
			processProvider = null;
			if (attachProgramOptionsAggregator != null) {
				attachProgramOptionsAggregator.AttachProgramOptionsAdded -= AttachProgramOptionsAggregator_AttachProgramOptionsAdded;
				attachProgramOptionsAggregator.Completed -= AttachProgramOptionsAggregator_Completed;
				attachProgramOptionsAggregator.Dispose();
				attachProgramOptionsAggregator = null;
				OnPropertyChanged(nameof(IsRefreshing));
			}
		}

		void AttachProgramOptionsAggregator_AttachProgramOptionsAdded(object sender, AttachProgramOptionsAddedEventArgs e) {
			uiDispatcher.VerifyAccess();
			if (attachProgramOptionsAggregator != sender)
				return;
			foreach (var options in e.AttachProgramOptions) {
				if (!dbgManager.CanDebugRuntime(options.ProcessId, options.RuntimeId))
					continue;
				var vm = new ProgramVM(processProvider, options, attachToProcessContext);
				realAllItems.Add(vm);
				if (IsMatch(vm, filterText)) {
					int index = GetInsertionIndex(vm, AllItems);
					AllItems.Insert(index, vm);
				}
			}
		}

		int GetInsertionIndex(ProgramVM vm, IList<ProgramVM> list) {
			var comparer = ProgramVMComparer.Instance;
			int lo = 0, hi = list.Count - 1;
			while (lo <= hi) {
				int index = (lo + hi) / 2;

				int c = comparer.Compare(vm, list[index]);
				if (c < 0)
					hi = index - 1;
				else if (c > 0)
					lo = index + 1;
				else
					return index;
			}
			return hi + 1;
		}

		sealed class ProgramVMComparer : IComparer<ProgramVM> {
			public static readonly ProgramVMComparer Instance = new ProgramVMComparer();
			ProgramVMComparer() { }
			public int Compare(ProgramVM x, ProgramVM y) {
				var c = StringComparer.CurrentCultureIgnoreCase.Compare(x.Name, y.Name);
				if (c != 0)
					return c;
				c = x.Id - y.Id;
				if (c != 0)
					return c;
				return StringComparer.CurrentCultureIgnoreCase.Compare(x.RuntimeName, y.RuntimeName);
			}
		}

		void AttachProgramOptionsAggregator_Completed(object sender, EventArgs e) {
			uiDispatcher.VerifyAccess();
			if (attachProgramOptionsAggregator != sender)
				return;
			RemoveAggregator();
		}

		void ClearAllItems() {
			uiDispatcher.VerifyAccess();
			realAllItems.Clear();
			AllItems.Reset(Array.Empty<ProgramVM>());
		}

		void FilterList(string filterText) {
			uiDispatcher.VerifyAccess();
			if (string.IsNullOrWhiteSpace(filterText))
				filterText = string.Empty;
			attachToProcessContext.SearchMatcher.SetSearchText(filterText);

			var newList = new List<ProgramVM>(GetFilteredItems(filterText));
			newList.Sort(ProgramVMComparer.Instance);
			AllItems.Reset(newList);
		}

		IEnumerable<ProgramVM> GetFilteredItems(string filterText) {
			uiDispatcher.VerifyAccess();
			foreach (var vm in realAllItems) {
				if (IsMatch(vm, filterText))
					yield return vm;
			}
		}

		bool IsMatch(ProgramVM vm, string filterText) {
			Debug.Assert(uiDispatcher.CheckAccess());
			var allStrings = new string[] {
				vm.Id.ToString(),
				vm.RuntimeName,
				vm.Name,
				vm.Title,
				vm.Filename,
				vm.Architecture,
			};
			return attachToProcessContext.SearchMatcher.IsMatchAll(allStrings);
		}

		internal void Dispose() {
			uiDispatcher.VerifyAccess();
			RemoveAggregator();
			ClearAllItems();
		}
	}
}
